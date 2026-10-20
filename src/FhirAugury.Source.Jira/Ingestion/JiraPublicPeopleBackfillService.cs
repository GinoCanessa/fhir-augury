using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Common.Text;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Process-local, one-use previews. The only writer is JiraDatabase's
/// conditional transaction, while the pipeline continues to own serialization.
/// </summary>
public sealed class JiraPublicPeopleBackfillService(
    JiraSource source,
    JiraDatabase database,
    JiraIngestionPipeline pipeline,
    JiraIndexBuilder indexBuilder,
    ILogger<JiraPublicPeopleBackfillService> logger,
    TimeProvider? timeProvider = null)
{
    public const int MaximumRequestedKeys = 2_000;
    public const int MaximumAffectedTickets = 10_000;
    public const int MaximumOutstandingPreviews = 8;
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _previewLock = new();
    private readonly Dictionary<string, PreviewEntry> _previews = new(StringComparer.Ordinal);
    private int _building;
    private int _applying;

    private sealed record PreviewEntry(JiraPeoplePlan Plan, DateTimeOffset ExpiresAt, long CompletedTimestamp);

    public async Task<JiraPublicPeoplePreviewResponse> PreviewAsync(
        JiraPublicPeoplePreviewRequest request, CancellationToken ct = default)
    {
        if (request is null || request.Keys is null
            || request.Keys.Count is 0 or > MaximumRequestedKeys
            || request.Keys.Any(key => !JiraPublicPeopleObservationReader.IsTicketKey(key))
            || request.Keys.Distinct(StringComparer.Ordinal).Count() != request.Keys.Count
            || request.EvidenceMode is not (JiraPublicPeopleEvidenceModes.CacheOnly or JiraPublicPeopleEvidenceModes.Upstream))
            return RefusePreview(JiraPublicPeopleCodes.InvalidRequest);

        string[] keys = request.Keys.Order(StringComparer.Ordinal).ToArray();
        lock (_previewLock)
        {
            PruneExpired();
            if (_previews.Count + _building + _applying >= MaximumOutstandingPreviews)
                return RefusePreview(JiraPublicPeopleCodes.PreviewCapacity);
            _building++;
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            if (pipeline.IsRunning)
                return RefusePreview(JiraPublicPeopleCodes.SourceBusy);
            using (SqliteConnection check = database.OpenConnection())
            {
                if (JiraDatabase.ReadSourceState(check).MutationInProgress)
                    return RefusePreview(JiraPublicPeopleCodes.SourceBusy);
            }

            JiraPeopleEvidence evidence = await source.ReadPublicPeopleEvidenceAsync(keys, request.EvidenceMode, ct);
            ct.ThrowIfCancellationRequested();
            using SqliteConnection connection = database.OpenConnection();
            using SqliteCommand transaction = connection.CreateCommand();
            transaction.CommandText = "BEGIN DEFERRED;";
            transaction.ExecuteNonQuery();
            JiraPeoplePlan plan = JiraPeoplePlanner.Build(
                JiraDatabase.ReadPublicPeopleState(connection, ct), keys, evidence, ct);
            transaction.CommandText = "COMMIT;";
            transaction.ExecuteNonQuery();
            ct.ThrowIfCancellationRequested();

            if (source.PublicPeopleSourceFingerprint != evidence.SourceFingerprint)
                return RefusePreview(JiraPublicPeopleCodes.StalePreview);
            JiraPublicPeoplePreviewResponse response = plan.Response;
            if (response.Code != JiraPublicPeopleCodes.PreviewReady)
            {
                logger.LogInformation("Jira public people preview refused: {Code}", response.Code);
                return response;
            }

            lock (_previewLock)
            {
                ct.ThrowIfCancellationRequested();
                // Slow upstream reads consume a slot, not the finished preview's
                // lifetime. Nothing evicts an in-flight acquisition as "expired".
                DateTimeOffset expiresAt = _clock.GetUtcNow() + PreviewLifetime;
                string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                _previews.Add(token, new(plan, expiresAt, _clock.GetTimestamp()));
                return response with { PreviewToken = token, ExpiresAt = expiresAt };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return RefusePreview(JiraPublicPeopleCodes.Cancelled);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            return RefusePreview(JiraPublicPeopleCodes.SourceBusy);
        }
        catch (SqliteException ex)
        {
            logger.LogError("Jira public people preview failed: {Code}; SQLite error code: {SqliteErrorCode}",
                JiraPublicPeopleCodes.InternalError, ex.SqliteErrorCode);
            return new() { Code = JiraPublicPeopleCodes.InternalError };
        }
        finally
        {
            lock (_previewLock) _building--;
        }
    }

    public async Task<JiraPublicPeopleApplyResponse> ApplyAsync(
        JiraPublicPeopleApplyRequest request, CancellationToken ct = default)
    {
        if (request is null || request.PreviewToken is not { Length: 64 }
            || request.PreviewToken.Any(value => !char.IsAsciiHexDigit(value)))
            return RefuseApply(JiraPublicPeopleCodes.InvalidRequest);

        PreviewEntry entry;
        lock (_previewLock)
        {
            PruneExpired();
            if (!_previews.TryGetValue(request.PreviewToken, out PreviewEntry? found))
                return RefuseApply(JiraPublicPeopleCodes.PreviewExpired);
            if (found.Plan.Response.RequiresSharedUserImpactAcknowledgement && !request.AcknowledgeSharedUserImpact)
                return RefuseApply(JiraPublicPeopleCodes.SharedImpactAcknowledgementRequired);
            entry = found;
            // Consume before attempting the writer. A lost response is never
            // evidence that replaying an apply is safe.
            _previews.Remove(request.PreviewToken);
            _applying++;
        }

        JiraPublicPeopleApplyResponse? committedResult = null;
        try
        {
            using IDisposable? gate = await pipeline.TryAcquirePublicPeopleMaintenanceAsync(ct);
            if (gate is null)
                return RefuseApply(JiraPublicPeopleCodes.SourceBusy);
            if (IsExpired(entry))
                return RefuseApply(JiraPublicPeopleCodes.PreviewExpired);
            if (entry.Plan.Evidence.SourceFingerprint != source.PublicPeopleSourceFingerprint)
                return RefuseApply(JiraPublicPeopleCodes.StalePreview);

            JiraPublicPeopleApplyResponse result = database.ApplyPublicPeople(entry.Plan, indexBuilder, ct);
            if (result.Code is JiraPublicPeopleCodes.Applied or JiraPublicPeopleCodes.NoChange)
            {
                committedResult = result;
                // COMMIT is known. Invalidate before releasing the pipeline gate,
                // even when the HTTP request has just been cancelled.
                source.ClearUserCache();
            }
            logger.LogInformation("Jira public people apply completed: {Code}; changed issue rows: {Count}",
                result.Code, result.Changes?.IssueRowsUpdated ?? 0);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (committedResult is not null)
            {
                logger.LogWarning("Jira public people apply committed; post-commit cancellation observed: {Code}",
                    committedResult.Code);
                return committedResult;
            }
            return RefuseApply(JiraPublicPeopleCodes.Cancelled);
        }
        catch (SqliteException ex) when (committedResult is null && ex.SqliteErrorCode is 5 or 6)
        {
            return RefuseApply(JiraPublicPeopleCodes.SourceBusy);
        }
        catch (SqliteException ex) when (committedResult is null)
        {
            logger.LogError("Jira public people apply failed: {Code}; SQLite error code: {SqliteErrorCode}",
                JiraPublicPeopleCodes.InternalError, ex.SqliteErrorCode);
            return new(JiraPublicPeopleCodes.InternalError);
        }
        finally
        {
            lock (_previewLock) _applying--;
        }
    }

    private void PruneExpired()
    {
        foreach (string key in _previews.Where(pair => IsExpired(pair.Value)).Select(pair => pair.Key).ToArray())
            _previews.Remove(key);
    }

    private bool IsExpired(PreviewEntry entry) =>
        entry.ExpiresAt <= _clock.GetUtcNow()
        || _clock.GetElapsedTime(entry.CompletedTimestamp) >= PreviewLifetime;

    private JiraPublicPeoplePreviewResponse RefusePreview(string code)
    {
        logger.LogInformation("Jira public people preview refused: {Code}", code);
        return new() { Code = code };
    }

    private JiraPublicPeopleApplyResponse RefuseApply(string code)
    {
        logger.LogInformation("Jira public people apply refused: {Code}", code);
        return new(code);
    }
}

internal sealed record JiraPeopleSourceBefore(long ContentRevision, bool MutationInProgress, string UpdatedAt);

internal sealed record JiraPeopleIssueBefore(
    string Table, int Id, string Key, string UpdatedAt,
    int? ReporterUserId, int? AssigneeUserId,
    string? Reporter, string? Assignee, string? VoteMover, string? VoteSeconder)
{
    internal string Shape => Table switch
    {
        "jira_issues" => "fhir",
        "jira_pss" => "pss",
        "jira_baldef" => "baldef",
        "jira_ballot" => "ballot",
        _ => "unknown",
    };
}

internal sealed record JiraPeopleRequesterBefore(int Id, string Key, int UserId);
internal sealed record JiraPeopleState(
    JiraPeopleSourceBefore Source,
    IReadOnlyList<JiraPeopleIssueBefore> Issues,
    IReadOnlyList<JiraUserRecord> Users,
    IReadOnlyList<JiraPeopleRequesterBefore> Requesters);

internal sealed record JiraPeopleUserChange(string Identity, string DisplayName, JiraUserRecord? Before);
internal sealed record JiraPeopleIssueChange(JiraPeopleIssueBefore Before, string? ReporterIdentity, string? AssigneeIdentity);
internal sealed record JiraPeopleRequesterAddition(string Key, string Identity);

internal sealed record JiraPeopleReference(
    string Table, string Key, int? IssueId, string? UpdatedAt,
    string Role, int? AssociationId, int? UserId, string? LegacyValue);

internal sealed record JiraPeoplePlan(
    IReadOnlyList<string> Keys,
    JiraPeopleEvidence Evidence,
    JiraPeopleSourceBefore Source,
    string Fingerprint,
    string ImpactFingerprint,
    IReadOnlyList<JiraPeopleUserChange> Users,
    IReadOnlyList<JiraPeopleIssueChange> Issues,
    IReadOnlyList<JiraPeopleRequesterAddition> Requesters,
    JiraPublicPeoplePreviewResponse Response);

/// <summary>
/// Pure planning over one SQLite read snapshot. Apply repeats this exact
/// calculation inside BEGIN IMMEDIATE using the captured evidence, not the
/// cache or upstream. Relevant before-images include missing exact identities.
/// </summary>
internal static class JiraPeoplePlanner
{
    internal static JiraPeoplePlan Build(
        JiraPeopleState state, IReadOnlyList<string> keys, JiraPeopleEvidence evidence, CancellationToken ct)
    {
        HashSet<string> selectedKeys = new(keys, StringComparer.Ordinal);
        ILookup<string, JiraPeopleIssueBefore> issuesByKey = state.Issues.ToLookup(issue => issue.Key, StringComparer.Ordinal);
        Dictionary<int, JiraUserRecord> usersById = state.Users.ToDictionary(user => user.Id);
        Dictionary<string, JiraUserRecord> usersByIdentity = state.Users.ToDictionary(user => user.Username, StringComparer.Ordinal);
        ILookup<string, JiraPeopleRequesterBefore> requestersByKey = state.Requesters.ToLookup(row => row.Key, StringComparer.Ordinal);
        List<JiraPeopleIssueBefore> selectedIssues = [];
        List<JiraPublicPeopleTicketResult> tickets = [];
        HashSet<int> relevantUserIds = [];
        HashSet<string> observedIdentities = new(StringComparer.Ordinal);
        Dictionary<string, string> observedNames = new(StringComparer.Ordinal);
        Dictionary<string, JiraPeopleUserChange> userChanges = new(StringComparer.Ordinal);
        Dictionary<string, JiraPeopleIssueChange> issueChanges = new(StringComparer.Ordinal);
        HashSet<JiraPeopleRequesterAddition> requesterAdditions = [];
        string? failure = state.Source.MutationInProgress ? JiraPublicPeopleCodes.SourceBusy : null;

        void Refuse(string code)
        {
            if (failure is null || code == JiraPublicPeopleCodes.UpstreamUnavailable)
                failure = code;
        }

        JiraPublicPeopleRoleResult Observe(
            JiraPeopleIssueBefore issue, string role, int? ordinal, JiraPersonObservation observation, int? binding)
        {
            if (binding is int boundId) relevantUserIds.Add(boundId);
            string? reason = JiraPublicPeopleObservationReader.FieldReason(observation.State);
            if (reason is not null)
            {
                if (observation.State == JiraPeopleFieldState.Absent && binding is not null)
                    reason = JiraPublicPeopleCodes.IdentityConflict;
                if (reason is JiraPublicPeopleCodes.IdentityConflict or JiraPublicPeopleCodes.ConflictingObservations
                    or JiraPublicPeopleCodes.MalformedValue)
                    Refuse(reason);
                return new(role, ordinal, [reason]);
            }
            if (observation.Identity is not string identity)
                return new(role, ordinal, [JiraPublicPeopleCodes.MissingIdentityBinding]);

            observedIdentities.Add(identity);
            usersByIdentity.TryGetValue(identity, out JiraUserRecord? user);
            if (user is not null) relevantUserIds.Add(user.Id);
            if ((user is not null && !user.HasAccountUsername)
                || (binding is not null && (user is null || user.Id != binding)))
            {
                Refuse(JiraPublicPeopleCodes.IdentityConflict);
                return new(role, ordinal, [JiraPublicPeopleCodes.IdentityConflict]);
            }
            if (observation.ExplicitName is string rawName)
            {
                if (observedNames.TryGetValue(identity, out string? otherName) && otherName != rawName)
                {
                    Refuse(JiraPublicPeopleCodes.ConflictingObservations);
                    return new(role, ordinal, [JiraPublicPeopleCodes.ConflictingObservations]);
                }
                observedNames[identity] = rawName;
            }
            if (string.IsNullOrWhiteSpace(observation.ExplicitName))
                return new(role, ordinal, [JiraPublicPeopleCodes.MissingExplicitNameEvidence]);
            string? name = PublicDisplayNamePolicy.Normalize(observation.ExplicitName, identity);
            if (name is null)
                return new(role, ordinal, [JiraPublicPeopleCodes.PolicyRejected]);

            bool changeUser = user is null || user.DisplayName != name || !user.HasExplicitDisplayName;
            if (changeUser)
                userChanges[identity] = new(identity, name, user);
            if (binding is null)
            {
                if (role == "in-person-requester")
                {
                    requesterAdditions.Add(new(issue.Key, identity));
                }
                else
                {
                    if (!issueChanges.TryGetValue(issue.Key, out JiraPeopleIssueChange? change))
                        change = new(issue, null, null);
                    issueChanges[issue.Key] = role == "reporter"
                        ? change with { ReporterIdentity = identity }
                        : change with { AssigneeIdentity = identity };
                }
            }
            return new(role, ordinal, binding is null
                ? [JiraPublicPeopleCodes.AvailablePublicName, JiraPublicPeopleCodes.MissingIdentityBinding]
                : [JiraPublicPeopleCodes.AvailablePublicName],
                binding is null, changeUser || binding is null);
        }

        foreach (string key in keys)
        {
            ct.ThrowIfCancellationRequested();
            JiraPeopleIssueBefore[] matches = issuesByKey[key].ToArray();
            if (matches.Length != 1)
            {
                string reason = matches.Length == 0 ? JiraPublicPeopleCodes.IssueNotFound : JiraPublicPeopleCodes.IdentityConflict;
                Refuse(reason);
                tickets.Add(new(key, "unknown", RoleFailures(reason)));
                continue;
            }
            JiraPeopleIssueBefore issue = matches[0];
            selectedIssues.Add(issue);
            if (issue.ReporterUserId is int reporterId) relevantUserIds.Add(reporterId);
            if (issue.AssigneeUserId is int assigneeId) relevantUserIds.Add(assigneeId);
            JiraPeopleRequesterBefore[] existingRequesters = requestersByKey[key].ToArray();
            foreach (JiraPeopleRequesterBefore row in existingRequesters) relevantUserIds.Add(row.UserId);
            JiraPeopleRevisionObservation revision = JiraPublicPeopleObservationReader.ReadForRevision(evidence, key, issue.UpdatedAt);
            if (revision.Observation is not JiraPeopleObservation observation)
            {
                string reason = revision.Failure ?? JiraPublicPeopleCodes.MissingObservation;
                Refuse(reason);
                tickets.Add(new(key, issue.Shape, RoleFailures(reason)));
                continue;
            }

            List<JiraPublicPeopleRoleResult> roles =
            [
                Observe(issue, "reporter", null, observation.Reporter, issue.ReporterUserId),
                Observe(issue, "assignee", null, observation.Assignee, issue.AssigneeUserId),
            ];
            JiraRequesterObservation requesters = observation.Requesters;
            string? requesterFailure = JiraPublicPeopleObservationReader.FieldReason(requesters.State);
            if (requesters.State == JiraPeopleFieldState.Absent && existingRequesters.Length > 0)
                requesterFailure = JiraPublicPeopleCodes.IdentityConflict;
            if (requesters.State == JiraPeopleFieldState.Present)
            {
                HashSet<string> identities = new(StringComparer.Ordinal);
                foreach (JiraPersonObservation person in requesters.People)
                {
                    if (person.State != JiraPeopleFieldState.Present || person.Identity is null)
                    {
                        requesterFailure = JiraPublicPeopleObservationReader.FieldReason(person.State)
                            ?? JiraPublicPeopleCodes.MissingIdentityBinding;
                        break;
                    }
                    if (!identities.Add(person.Identity))
                    {
                        requesterFailure = JiraPublicPeopleCodes.ConflictingObservations;
                        break;
                    }
                }
                if (existingRequesters.Any(row => !usersById.TryGetValue(row.UserId, out JiraUserRecord? user)
                    || !user.HasAccountUsername || !identities.Contains(user.Username)))
                    requesterFailure = JiraPublicPeopleCodes.IdentityConflict;
            }

            if (requesterFailure is not null)
            {
                if (requesterFailure is not (JiraPublicPeopleCodes.MissingField or JiraPublicPeopleCodes.RoleAbsent))
                    Refuse(requesterFailure);
                if (requesters.People.Count == 0)
                    roles.Add(new("in-person-requester", null, [requesterFailure]));
                else
                    roles.AddRange(requesters.People.Select((_, index) =>
                        new JiraPublicPeopleRoleResult("in-person-requester", index, [requesterFailure])));
            }
            else
            {
                for (int index = 0; index < requesters.People.Count; index++)
                {
                    JiraPersonObservation person = requesters.People[index];
                    int? binding = existingRequesters.FirstOrDefault(row =>
                        usersById[row.UserId].Username == person.Identity)?.UserId;
                    roles.Add(Observe(issue, "in-person-requester", index, person, binding));
                }
            }
            tickets.Add(new(key, issue.Shape, roles));
        }

        // The legacy users lookup joins by BOTH DisplayName and Username.
        // Include its entire name-collision closure as lookup impact, while
        // never using any of those matches to resolve a person binding.
        HashSet<string> aliases = new(StringComparer.Ordinal);
        HashSet<int> changedUserIds = [];
        foreach (JiraPeopleUserChange user in userChanges.Values)
        {
            aliases.Add(user.Identity);
            aliases.Add(user.DisplayName);
            if (user.Before is not null)
            {
                changedUserIds.Add(user.Before.Id);
                if (user.Before.DisplayName.Length > 0) aliases.Add(user.Before.DisplayName);
            }
        }
        bool expanded;
        do
        {
            ct.ThrowIfCancellationRequested();
            expanded = false;
            foreach (JiraUserRecord user in state.Users)
            {
                if (!aliases.Contains(user.Username) && !aliases.Contains(user.DisplayName)) continue;
                relevantUserIds.Add(user.Id);
                expanded |= aliases.Add(user.Username);
                if (user.DisplayName.Length > 0) expanded |= aliases.Add(user.DisplayName);
            }
        } while (expanded);

        List<JiraPeopleReference> references = [];
        foreach (JiraPeopleIssueBefore issue in state.Issues)
        {
            ct.ThrowIfCancellationRequested();
            issueChanges.TryGetValue(issue.Key, out JiraPeopleIssueChange? change);
            AddRole("reporter", issue.ReporterUserId, issue.Reporter, change?.ReporterIdentity is not null);
            AddRole("assignee", issue.AssigneeUserId, issue.Assignee, change?.AssigneeIdentity is not null);
            AddRole("vote-mover", null, issue.VoteMover, false);
            AddRole("vote-seconder", null, issue.VoteSeconder, false);

            void AddRole(string role, int? id, string? legacy, bool adding)
            {
                if (adding || (id is int userId && changedUserIds.Contains(userId))
                    || (legacy is not null && aliases.Contains(legacy)))
                    references.Add(new(issue.Table, issue.Key, issue.Id, issue.UpdatedAt, role, null, id, legacy));
            }
        }
        foreach (JiraPeopleRequesterBefore row in state.Requesters)
        {
            ct.ThrowIfCancellationRequested();
            usersById.TryGetValue(row.UserId, out JiraUserRecord? user);
            if (!changedUserIds.Contains(row.UserId)
                && (user is null || (!aliases.Contains(user.Username) && !aliases.Contains(user.DisplayName))))
                continue;
            JiraPeopleIssueBefore[] issues = issuesByKey[row.Key].ToArray();
            if (issues.Length == 0)
                references.Add(new("", row.Key, null, null, "in-person-requester", row.Id, row.UserId, null));
            foreach (JiraPeopleIssueBefore issue in issues)
                references.Add(new(issue.Table, row.Key, issue.Id, issue.UpdatedAt, "in-person-requester", row.Id, row.UserId, null));
        }
        foreach (JiraPeopleRequesterAddition addition in requesterAdditions)
        {
            JiraPeopleIssueBefore issue = selectedIssues.Single(value => value.Key == addition.Key);
            references.Add(new(issue.Table, issue.Key, issue.Id, issue.UpdatedAt,
                "in-person-requester", null, null, addition.Identity));
        }
        JiraPeopleReference[] orderedReferences = references
            .OrderBy(row => row.Table, StringComparer.Ordinal).ThenBy(row => row.Key, StringComparer.Ordinal)
            .ThenBy(row => row.Role, StringComparer.Ordinal).ThenBy(row => row.AssociationId)
            .ThenBy(row => row.LegacyValue, StringComparer.Ordinal).ToArray();
        JiraPublicPeopleAffectedTicket[] affected = orderedReferences
            .GroupBy(row => (row.Table, row.Key))
            .Select(group => new JiraPublicPeopleAffectedTicket(
                group.Key.Key,
                new JiraPeopleIssueBefore(group.Key.Table, 0, group.Key.Key, "", null, null, null, null, null, null).Shape,
                selectedKeys.Contains(group.Key.Key),
                group.Count(row => row.Role == "reporter"),
                group.Count(row => row.Role == "assignee"),
                group.Count(row => row.Role == "in-person-requester"),
                group.Count(row => row.Role == "vote-mover"),
                group.Count(row => row.Role == "vote-seconder"))).ToArray();
        if (affected.Length > JiraPublicPeopleBackfillService.MaximumAffectedTickets)
        {
            Refuse(JiraPublicPeopleCodes.SharedImpactTooLarge);
            affected = [];
            tickets = tickets.Select(ticket => ticket with
            {
                Roles = ticket.Roles.Select(role => role with
                {
                    Reasons = role.Reasons.Append(JiraPublicPeopleCodes.SharedImpactTooLarge).ToArray(),
                }).ToArray(),
            }).ToList();
        }
        if (affected.Any(ticket => !JiraPublicPeopleObservationReader.IsTicketKey(ticket.Key)))
        {
            Refuse(JiraPublicPeopleCodes.IdentityConflict);
            affected = [];
        }

        JiraPeopleUserChange[] changedUsers = userChanges.Values.OrderBy(user => user.Identity, StringComparer.Ordinal).ToArray();
        JiraPeopleIssueChange[] changedIssues = issueChanges.Values.OrderBy(issue => issue.Before.Key, StringComparer.Ordinal).ToArray();
        JiraPeopleRequesterAddition[] addedRequesters = requesterAdditions.OrderBy(row => row.Key, StringComparer.Ordinal)
            .ThenBy(row => row.Identity, StringComparer.Ordinal).ToArray();
        JiraPublicPeopleChanges changes = new(
            changedUsers.Count(user => user.Before is null), changedUsers.Count(user => user.Before is not null),
            changedIssues.Length, addedRequesters.Length);
        string impactFingerprint = JiraPublicPeopleObservationReader.Digest(JsonSerializer.Serialize(orderedReferences));
        string fingerprint = JiraPublicPeopleObservationReader.Digest(JsonSerializer.Serialize(new
        {
            ObservationVersion = JiraPublicPeopleObservationReader.CurrentVersion,
            PolicyVersion = PublicDisplayNamePolicy.CurrentVersion,
            Evidence = evidence,
            state.Source,
            SelectedIssues = selectedIssues,
            SelectedRequesters = state.Requesters.Where(row => selectedKeys.Contains(row.Key)).OrderBy(row => row.Id).ToArray(),
            ObservedIdentities = observedIdentities.Order(StringComparer.Ordinal).ToArray(),
            RelevantUserIds = relevantUserIds.Order().ToArray(),
            Users = state.Users.Where(user => relevantUserIds.Contains(user.Id)).OrderBy(user => user.Id).ToArray(),
            References = orderedReferences,
            UsersToChange = changedUsers,
            IssuesToChange = changedIssues,
            RequestersToAdd = addedRequesters,
        }));
        return new(keys, evidence, state.Source, fingerprint, impactFingerprint, changedUsers, changedIssues, addedRequesters, new()
        {
            Code = failure ?? JiraPublicPeopleCodes.PreviewReady,
            ContentRevision = state.Source.ContentRevision,
            Tickets = tickets,
            AffectedTickets = affected,
            RequiresSharedUserImpactAcknowledgement = affected.Any(ticket => !ticket.IsSelected),
            Changes = changes,
        });
    }

    private static IReadOnlyList<JiraPublicPeopleRoleResult> RoleFailures(string reason) =>
    [
        new("reporter", null, [reason]),
        new("assignee", null, [reason]),
        new("in-person-requester", null, [reason]),
    ];
}
