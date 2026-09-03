using FhirAugury.Common.WorkGroups;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Tools.TicketMdToDb.Compilation;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketMdToDb.Import;

public sealed record ReadinessFailure(
    string Code,
    string Message,
    string? TicketKey,
    string? SourcePath,
    bool Waivable);

public sealed record ReadinessVerificationResult(
    IReadOnlyList<ReadinessFailure> Failures,
    IReadOnlyList<string> FullyReadyKeys,
    IReadOnlyList<string> VisitedWorkGroups,
    IReadOnlyList<string> VisitedPartitions)
{
    public bool HasFatalFailures => Failures.Any(failure => !failure.Waivable);
    public bool HasWaivableFailures => Failures.Any(failure => failure.Waivable);
    public bool CanPromote(bool acceptUnresolvedHydration) =>
        !HasFatalFailures && (!HasWaivableFailures || acceptUnresolvedHydration);
}

public static class ReadinessVerifier
{
    private static readonly HashSet<string> CanonicalTicketTypes = new(StringComparer.Ordinal)
    {
        "Comment",
        "Question",
        "Technical Correction",
        "Change Request",
    };

    public static IReadOnlyList<ReadinessFailure> VerifyPreparedProjection(
        ImportManifest manifest,
        DateTimeOffset importedAt,
        PersistedDatabaseState state)
    {
        List<ReadinessFailure> failures = [];
        Dictionary<string, string> sourcePaths = manifest.Tickets.ToDictionary(
            ticket => ticket.Key,
            ticket => ticket.SourcePath,
            StringComparer.Ordinal);
        string[] manifestKeys = manifest.Tickets
            .Select(ticket => ticket.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        string[] persistedKeys = state.PreparedTickets.Keys
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        if (!manifestKeys.SequenceEqual(persistedKeys, StringComparer.Ordinal))
        {
            failures.Add(Fatal(
                "prepared-key-set-mismatch",
                $"Prepared keys [{string.Join(", ", persistedKeys)}] do not equal manifest keys [{string.Join(", ", manifestKeys)}]."));
        }

        foreach (ManifestTicket ticket in manifest.Tickets)
        {
            if (!state.PreparedTickets.TryGetValue(ticket.Key, out PersistedPreparedTicket? persisted))
            {
                continue;
            }

            ComparePayload(
                ticket.Payload,
                persisted.Payload,
                importedAt,
                ticket.Key,
                ticket.SourcePath,
                failures);
            if (!PreparedTicketImpactValues.Supported.Contains(persisted.Payload.ProposalAImpact))
            {
                failures.Add(Fatal(
                    "prepared-impact-invalid",
                    $"ProposalAImpact is not supported: {persisted.Payload.ProposalAImpact}",
                    ticket.Key,
                    ticket.SourcePath));
            }
            if (!PreparedTicketImpactValues.Supported.Contains(persisted.Payload.ProposalBImpact))
            {
                failures.Add(Fatal(
                    "prepared-impact-invalid",
                    $"ProposalBImpact is not supported: {persisted.Payload.ProposalBImpact}",
                    ticket.Key,
                    ticket.SourcePath));
            }
            if (!PreparedTicketRecommendationValues.Supported.Contains(persisted.Payload.Recommendation))
            {
                failures.Add(Fatal(
                    "prepared-recommendation-invalid",
                    $"Recommendation is not supported: {persisted.Payload.Recommendation}",
                    ticket.Key,
                    ticket.SourcePath));
            }
        }

        foreach (string orphan in state.OrphanPreparedChildren)
        {
            failures.Add(Fatal(
                "prepared-orphan-child",
                $"Prepared child row has no parent: {orphan}"));
        }
        ValidateIdentities(state.Identities, sourcePaths, failures);
        return failures;
    }

    public static async Task<ReadinessVerificationResult> VerifyAsync(
        ImportManifest manifest,
        DateTimeOffset importedAt,
        IReadOnlyDictionary<string, HydrationBatch> expectedHydration,
        IReadOnlyList<HydrationAttemptFailure> hydrationFailures,
        IReadOnlyDictionary<string, JiraProcessingSourceTicketRecord> expectedSourceTickets,
        PersistedDatabaseState state,
        PreparerDatabase database,
        string databasePath,
        CancellationToken ct = default)
    {
        List<ReadinessFailure> failures =
            [.. VerifyPreparedProjection(manifest, importedAt, state)];
        Dictionary<string, string> sourcePaths = manifest.Tickets.ToDictionary(
            ticket => ticket.Key,
            ticket => ticket.SourcePath,
            StringComparer.Ordinal);

        foreach (HydrationAttemptFailure failure in hydrationFailures)
        {
            failures.Add(Fatal(
                "hydration-coordinator-failed",
                $"{failure.FailureCode}: {failure.ExceptionType}: {failure.Reason}",
                failure.TicketKey,
                sourcePaths.GetValueOrDefault(failure.TicketKey)));
        }

        VerifyHydrationProjection(
            expectedHydration,
            state,
            sourcePaths,
            failures);
        VerifySourceTicketProjection(
            expectedSourceTickets,
            state.SourceTickets,
            sourcePaths,
            failures);

        Dictionary<string, HydrationJiraRow> readySelfRows = [];
        foreach (ManifestTicket ticket in manifest.Tickets)
        {
            if (!expectedHydration.TryGetValue(ticket.Key, out HydrationBatch? batch))
            {
                continue;
            }

            bool ready = true;
            if (!string.Equals(batch.Parent.HydrationStatus, "resolved", StringComparison.Ordinal))
            {
                failures.Add(Waivable(
                    "parent-hydration-unresolved",
                    batch.Parent.HydrationReason ?? "Parent hydration is unresolved.",
                    ticket.Key,
                    ticket.SourcePath));
                ready = false;
            }
            if (string.IsNullOrWhiteSpace(batch.Parent.Specification))
            {
                failures.Add(Waivable(
                    "parent-specification-missing",
                    "Parent hydration has no specification.",
                    ticket.Key,
                    ticket.SourcePath));
                ready = false;
            }

            HydrationJiraRow[] selfRows = batch.JiraRows
                .Where(row => string.Equals(row.JiraKey, ticket.Key, StringComparison.Ordinal))
                .ToArray();
            if (selfRows.Length != 1)
            {
                failures.Add(Fatal(
                    "self-hydration-cardinality",
                    $"Expected one self Jira hydration row but found {selfRows.Length}.",
                    ticket.Key,
                    ticket.SourcePath));
                continue;
            }

            HydrationJiraRow self = selfRows[0];
            if (!string.Equals(self.HydrationStatus, "resolved", StringComparison.Ordinal))
            {
                failures.Add(Waivable(
                    "self-hydration-unresolved",
                    self.HydrationReason ?? "Self Jira hydration is unresolved.",
                    ticket.Key,
                    ticket.SourcePath));
                ready = false;
            }

            ready &= RequireMetadata(self.Title, "title", ticket, failures);
            ready &= RequireMetadata(self.Status, "status", ticket, failures);
            ready &= RequireMetadata(self.Type, "type", ticket, failures);
            ready &= RequireMetadata(self.WorkGroup, "workgroup", ticket, failures);
            ready &= RequireMetadata(self.Specification, "specification", ticket, failures);
            if (!string.IsNullOrWhiteSpace(self.Type)
                && !CanonicalTicketTypes.Contains(self.Type))
            {
                failures.Add(Fatal(
                    "self-type-invalid",
                    $"Self Jira type '{self.Type}' is not a canonical topic-grouping type.",
                    ticket.Key,
                    ticket.SourcePath));
                ready = false;
            }

            if (ready)
            {
                readySelfRows.Add(ticket.Key, self);
            }
        }

        await VerifyIntegrityAsync(databasePath, failures, ct);
        (IReadOnlyList<string> workGroups, IReadOnlyList<string> partitions) =
            await VerifyClusteringAsync(
                readySelfRows,
                database,
                sourcePaths,
                failures,
                ct);

        return new ReadinessVerificationResult(
            failures,
            readySelfRows.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
            workGroups,
            partitions);
    }

    private static void ComparePayload(
        PreparedTicketPayload expected,
        PreparedTicketPayload actual,
        DateTimeOffset importedAt,
        string key,
        string sourcePath,
        ICollection<ReadinessFailure> failures)
    {
        CompareScalar(nameof(actual.Key), expected.Key, actual.Key);
        CompareScalar(nameof(actual.RequestSummary), expected.RequestSummary, actual.RequestSummary);
        CompareScalar(nameof(actual.CommentSummary), expected.CommentSummary, actual.CommentSummary);
        CompareScalar(nameof(actual.LinkedTicketSummary), expected.LinkedTicketSummary, actual.LinkedTicketSummary);
        CompareScalar(nameof(actual.RelatedTicketSummary), expected.RelatedTicketSummary, actual.RelatedTicketSummary);
        CompareScalar(nameof(actual.RelatedZulipSummary), expected.RelatedZulipSummary, actual.RelatedZulipSummary);
        CompareScalar(nameof(actual.RelatedGitHubSummary), expected.RelatedGitHubSummary, actual.RelatedGitHubSummary);
        CompareScalar(nameof(actual.ExistingProposed), expected.ExistingProposed, actual.ExistingProposed);
        CompareScalar(nameof(actual.ProposalA), expected.ProposalA, actual.ProposalA);
        CompareScalar(nameof(actual.ProposalAJustification), expected.ProposalAJustification, actual.ProposalAJustification);
        CompareScalar(nameof(actual.ProposalAImpact), expected.ProposalAImpact, actual.ProposalAImpact);
        CompareScalar(nameof(actual.ProposalB), expected.ProposalB, actual.ProposalB);
        CompareScalar(nameof(actual.ProposalBJustification), expected.ProposalBJustification, actual.ProposalBJustification);
        CompareScalar(nameof(actual.ProposalBImpact), expected.ProposalBImpact, actual.ProposalBImpact);
        CompareScalar(nameof(actual.ProposalC), expected.ProposalC, actual.ProposalC);
        CompareScalar(nameof(actual.ProposalCJustification), expected.ProposalCJustification, actual.ProposalCJustification);
        CompareScalar(nameof(actual.Recommendation), expected.Recommendation, actual.Recommendation);
        CompareScalar(
            nameof(actual.RecommendationJustification),
            expected.RecommendationJustification,
            actual.RecommendationJustification);
        if (actual.SavedAt != importedAt)
        {
            failures.Add(Fatal(
                "prepared-scalar-mismatch",
                $"SavedAt expected '{importedAt:O}' but found '{actual.SavedAt:O}'.",
                key,
                sourcePath));
        }

        CompareCollections(
            "Repos",
            expected.Repos.Select(row => $"{row.Repo}\u001f{row.RepoCategory}\u001f{row.Justification}"),
            actual.Repos.Select(row => $"{row.Repo}\u001f{row.RepoCategory}\u001f{row.Justification}"));
        CompareCollections(
            "RelatedJiraTickets",
            expected.RelatedJiraTickets.Select(
                row => $"{row.AssociatedTicketKey}\u001f{row.LinkType}\u001f{row.Justification}"),
            actual.RelatedJiraTickets.Select(
                row => $"{row.AssociatedTicketKey}\u001f{row.LinkType}\u001f{row.Justification}"));
        CompareCollections(
            "RelatedZulipThreads",
            expected.RelatedZulipThreads.Select(
                row => $"{row.ZulipThreadId}\u001f{row.Justification}"),
            actual.RelatedZulipThreads.Select(
                row => $"{row.ZulipThreadId}\u001f{row.Justification}"));
        CompareCollections(
            "RelatedGitHubItems",
            expected.RelatedGitHubItems.Select(
                row => $"{row.GitHubItemId}\u001f{row.Justification}"),
            actual.RelatedGitHubItems.Select(
                row => $"{row.GitHubItemId}\u001f{row.Justification}"));
        return;

        void CompareScalar(string field, string expectedValue, string actualValue)
        {
            if (!string.Equals(expectedValue, actualValue, StringComparison.Ordinal))
            {
                failures.Add(Fatal(
                    "prepared-scalar-mismatch",
                    $"{field} expected '{expectedValue}' but found '{actualValue}'.",
                    key,
                    sourcePath));
            }
        }

        void CompareCollections(string field, IEnumerable<string> expectedRows, IEnumerable<string> actualRows)
        {
            string[] normalizedExpected = expectedRows
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            string[] normalizedActual = actualRows
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (!normalizedExpected.SequenceEqual(normalizedActual, StringComparer.Ordinal))
            {
                failures.Add(Fatal(
                    "prepared-child-mismatch",
                    $"{field} does not match the manifest projection.",
                    key,
                    sourcePath));
            }
        }
    }

    private static void ValidateIdentities(
        IReadOnlyList<PersistedRowIdentity> identities,
        IReadOnlyDictionary<string, string> sourcePaths,
        ICollection<ReadinessFailure> failures)
    {
        foreach (PersistedRowIdentity identity in identities)
        {
            if (identity.RowId <= 0 || string.IsNullOrWhiteSpace(identity.Id))
            {
                failures.Add(Fatal(
                    "generated-identity-missing",
                    $"{identity.Table} row '{identity.NaturalKey}' has no usable generated identity.",
                    identity.TicketKey,
                    sourcePaths.GetValueOrDefault(identity.TicketKey)));
            }
        }

        foreach (IGrouping<string, PersistedRowIdentity> table in identities.GroupBy(
                     identity => identity.Table,
                     StringComparer.Ordinal))
        {
            foreach (IGrouping<long, PersistedRowIdentity> duplicate in table.GroupBy(
                         identity => identity.RowId)
                     .Where(group => group.Count() > 1))
            {
                failures.Add(Fatal(
                    "generated-rowid-duplicate",
                    $"{table.Key} contains duplicate RowId {duplicate.Key}."));
            }
            foreach (IGrouping<string, PersistedRowIdentity> duplicate in table.GroupBy(
                         identity => identity.Id,
                         StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
            {
                failures.Add(Fatal(
                    "generated-id-duplicate",
                    $"{table.Key} contains duplicate Id '{duplicate.Key}'."));
            }
        }
    }

    private static void VerifyHydrationProjection(
        IReadOnlyDictionary<string, HydrationBatch> expected,
        PersistedDatabaseState state,
        IReadOnlyDictionary<string, string> sourcePaths,
        ICollection<ReadinessFailure> failures)
    {
        CompareRows(
            "hydration-parent",
            expected.Values.Select(batch => batch.Parent),
            state.HydrationParents.Select(row => row.Row),
            row => row.TicketKey,
            sourcePaths,
            failures);
        CompareRows(
            "hydration-jira",
            expected.Values.SelectMany(batch => batch.JiraRows),
            state.HydrationJiraRows.Select(row => row.Row),
            row => $"{row.TicketKey}\u001f{row.JiraKey}",
            sourcePaths,
            failures);
        CompareRows(
            "hydration-zulip",
            expected.Values.SelectMany(batch => batch.ZulipRows),
            state.HydrationZulipRows.Select(row => row.Row),
            row => $"{row.TicketKey}\u001f{row.ZulipThreadId}",
            sourcePaths,
            failures);
        CompareRows(
            "hydration-github",
            expected.Values.SelectMany(batch => batch.GitHubRows),
            state.HydrationGitHubRows.Select(row => row.Row),
            row => $"{row.TicketKey}\u001f{row.GitHubItemId}",
            sourcePaths,
            failures);
        CompareRows(
            "hydration-repo",
            expected.Values.SelectMany(batch => batch.RepoRows),
            state.HydrationRepoRows.Select(row => row.Row),
            row => $"{row.TicketKey}\u001f{row.Repo}",
            sourcePaths,
            failures);
        CompareRows(
            "hydration-jira-xref",
            expected.Values.SelectMany(batch => batch.JiraXrefRows),
            state.HydrationJiraXrefRows.Select(row => row.Row),
            row => $"{row.TicketKey}\u001f{row.JiraKey}\u001f{row.Source}",
            sourcePaths,
            failures);

        foreach (PersistedHydrationJira persisted in state.HydrationJiraRows)
        {
            string expectedClean = Hl7WorkGroupNameCleaner.Clean(persisted.Row.WorkGroup);
            string? normalized = string.IsNullOrEmpty(expectedClean) ? null : expectedClean;
            if (!string.Equals(normalized, persisted.WorkGroupClean, StringComparison.Ordinal))
            {
                failures.Add(Fatal(
                    "hydration-workgroup-clean-mismatch",
                    $"WorkGroupClean expected '{normalized}' but found '{persisted.WorkGroupClean}'.",
                    persisted.Row.TicketKey,
                    sourcePaths.GetValueOrDefault(persisted.Row.TicketKey)));
            }
        }
    }

    private static void CompareRows<T>(
        string kind,
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        Func<T, string> identity,
        IReadOnlyDictionary<string, string> sourcePaths,
        ICollection<ReadinessFailure> failures)
        where T : notnull
    {
        T[] expectedRows = expected.OrderBy(identity, StringComparer.Ordinal).ToArray();
        T[] actualRows = actual.OrderBy(identity, StringComparer.Ordinal).ToArray();
        if (expectedRows.SequenceEqual(actualRows))
        {
            return;
        }

        string? ticketKey = expectedRows
            .Select(identity)
            .Concat(actualRows.Select(identity))
            .Select(value => value.Split('\u001f')[0])
            .FirstOrDefault();
        failures.Add(Fatal(
            $"{kind}-mismatch",
            $"Persisted {kind} rows do not exactly match the returned hydration batches.",
            ticketKey,
            ticketKey is null ? null : sourcePaths.GetValueOrDefault(ticketKey)));
    }

    private static void VerifySourceTicketProjection(
        IReadOnlyDictionary<string, JiraProcessingSourceTicketRecord> expected,
        IReadOnlyList<JiraProcessingSourceTicketRecord> actualRows,
        IReadOnlyDictionary<string, string> sourcePaths,
        ICollection<ReadinessFailure> failures)
    {
        Dictionary<string, JiraProcessingSourceTicketRecord> actual = actualRows
            .Where(row => string.Equals(row.SourceTicketShape, "fhir", StringComparison.Ordinal))
            .ToDictionary(row => row.Key, StringComparer.Ordinal);
        string[] expectedKeys = expected.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        string[] actualKeys = actual.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (!expectedKeys.SequenceEqual(actualKeys, StringComparer.Ordinal))
        {
            failures.Add(Fatal(
                "source-ticket-key-set-mismatch",
                $"Projected source keys [{string.Join(", ", actualKeys)}] do not equal expected keys [{string.Join(", ", expectedKeys)}]."));
        }

        foreach ((string key, JiraProcessingSourceTicketRecord expectedRow) in expected)
        {
            if (!actual.TryGetValue(key, out JiraProcessingSourceTicketRecord? actualRow))
            {
                continue;
            }

            bool equal =
                string.Equals(expectedRow.Id, actualRow.Id, StringComparison.Ordinal)
                && string.Equals(expectedRow.Key, actualRow.Key, StringComparison.Ordinal)
                && string.Equals(expectedRow.Title, actualRow.Title, StringComparison.Ordinal)
                && string.Equals(expectedRow.Description, actualRow.Description, StringComparison.Ordinal)
                && string.Equals(expectedRow.Project, actualRow.Project, StringComparison.Ordinal)
                && string.Equals(expectedRow.Status, actualRow.Status, StringComparison.Ordinal)
                && string.Equals(expectedRow.WorkGroup, actualRow.WorkGroup, StringComparison.Ordinal)
                && string.Equals(expectedRow.Type, actualRow.Type, StringComparison.Ordinal)
                && string.Equals(expectedRow.Specification, actualRow.Specification, StringComparison.Ordinal)
                && string.Equals(expectedRow.SourceTicketShape, actualRow.SourceTicketShape, StringComparison.Ordinal)
                && expectedRow.LastSyncedAt == actualRow.LastSyncedAt
                && expectedRow.LastUpdated == actualRow.LastUpdated
                && expectedRow.StartedProcessingAt == actualRow.StartedProcessingAt
                && expectedRow.CompletedProcessingAt == actualRow.CompletedProcessingAt
                && expectedRow.LastProcessingAttemptAt == actualRow.LastProcessingAttemptAt
                && string.Equals(expectedRow.ProcessingStatus, actualRow.ProcessingStatus, StringComparison.Ordinal)
                && string.Equals(expectedRow.ProcessingError, actualRow.ProcessingError, StringComparison.Ordinal)
                && expectedRow.ProcessingAttemptCount == actualRow.ProcessingAttemptCount
                && string.Equals(expectedRow.CompletionId, actualRow.CompletionId, StringComparison.Ordinal)
                && string.Equals(expectedRow.ErrorMessage, actualRow.ErrorMessage, StringComparison.Ordinal)
                && expectedRow.AgentExitCode == actualRow.AgentExitCode
                && expectedRow.ErrorOccurredAt == actualRow.ErrorOccurredAt;
            if (!equal)
            {
                failures.Add(Fatal(
                    "source-ticket-field-mismatch",
                    "Projected local source-ticket fields or completion state do not match.",
                    key,
                    sourcePaths.GetValueOrDefault(key)));
            }
            if (!string.Equals(actualRow.ProcessingStatus, ProcessingStatusValues.Complete, StringComparison.Ordinal)
                || actualRow.CompletedProcessingAt is null
                || string.IsNullOrWhiteSpace(actualRow.CompletionId))
            {
                failures.Add(Fatal(
                    "source-ticket-not-complete",
                    "Projected local source ticket is not durably complete.",
                    key,
                    sourcePaths.GetValueOrDefault(key)));
            }
        }
    }

    private static async Task VerifyIntegrityAsync(
        string databasePath,
        ICollection<ReadinessFailure> failures,
        CancellationToken ct)
    {
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(ct);
        await using (SqliteCommand integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            string result = (await integrity.ExecuteScalarAsync(ct))?.ToString() ?? string.Empty;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(Fatal(
                    "sqlite-integrity-failed",
                    $"PRAGMA integrity_check returned '{result}'."));
            }
        }

        await using (SqliteCommand foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check";
            await using SqliteDataReader reader = await foreignKeys.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                failures.Add(Fatal(
                    "sqlite-foreign-key-failed",
                    "PRAGMA foreign_key_check returned one or more rows."));
            }
        }
    }

    private static async Task<(IReadOnlyList<string>, IReadOnlyList<string>)> VerifyClusteringAsync(
        IReadOnlyDictionary<string, HydrationJiraRow> readySelfRows,
        PreparerDatabase database,
        IReadOnlyDictionary<string, string> sourcePaths,
        ICollection<ReadinessFailure> failures,
        CancellationToken ct)
    {
        Dictionary<string, List<string>> expectedByWorkGroup = new(StringComparer.Ordinal);
        foreach ((string key, HydrationJiraRow self) in readySelfRows)
        {
            string clean = Hl7WorkGroupNameCleaner.Clean(self.WorkGroup);
            if (string.IsNullOrWhiteSpace(clean))
            {
                failures.Add(Fatal(
                    "clustering-workgroup-empty",
                    "Ready self Jira row produces an empty cleaned workgroup.",
                    key,
                    sourcePaths.GetValueOrDefault(key)));
                continue;
            }

            if (!expectedByWorkGroup.TryGetValue(clean, out List<string>? keys))
            {
                keys = [];
                expectedByWorkGroup.Add(clean, keys);
            }
            keys.Add(key);
        }

        List<string> visited = [];
        HashSet<string> partitions = new(StringComparer.Ordinal);
        Dictionary<string, int> readyOccurrences = readySelfRows.Keys.ToDictionary(
            key => key,
            _ => 0,
            StringComparer.Ordinal);
        foreach ((string clean, List<string> expectedKeys) in expectedByWorkGroup.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            visited.Add(clean);
            PreparedTicketClusteringSignals? signals =
                await database.GetClusteringSignalsAsync(clean, ct);
            if (signals is null)
            {
                failures.Add(Fatal(
                    "clustering-workgroup-missing",
                    $"GetClusteringSignalsAsync returned no data for {clean}."));
                continue;
            }
            if (string.IsNullOrWhiteSpace(signals.WorkGroupDisplay))
            {
                failures.Add(Fatal(
                    "clustering-workgroup-display-missing",
                    $"Workgroup {clean} has no display name."));
            }

            HashSet<string> signalKeys = signals.Tickets
                .Select(ticket => ticket.TicketKey)
                .ToHashSet(StringComparer.Ordinal);
            foreach (string key in expectedKeys)
            {
                if (!signalKeys.Contains(key))
                {
                    failures.Add(Fatal(
                        "clustering-ready-ticket-missing",
                        $"Ready ticket is absent from workgroup {clean}.",
                        key,
                        sourcePaths.GetValueOrDefault(key)));
                }
                else
                {
                    readyOccurrences[key]++;
                }
            }

            foreach (PreparedTicketClusteringSignal signal in signals.Tickets)
            {
                if (!signal.HasPreparedTicket)
                {
                    failures.Add(Fatal(
                        "clustering-prepared-ticket-missing",
                        "Clustering emitted a hydration-only ticket.",
                        signal.TicketKey,
                        sourcePaths.GetValueOrDefault(signal.TicketKey)));
                }
                if (string.IsNullOrWhiteSpace(signal.Specification)
                    || string.Equals(signal.Specification, "Unspecified", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(signal.Type)
                    || string.Equals(signal.Type, "Unspecified", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(Fatal(
                        "clustering-ticket-unpartitionable",
                        "Clustering ticket has missing or Unspecified partition metadata.",
                        signal.TicketKey,
                        sourcePaths.GetValueOrDefault(signal.TicketKey)));
                    continue;
                }
                if (!CanonicalTicketTypes.Contains(signal.Type))
                {
                    failures.Add(Fatal(
                        "clustering-type-invalid",
                        $"Clustering type '{signal.Type}' is not canonical.",
                        signal.TicketKey,
                        sourcePaths.GetValueOrDefault(signal.TicketKey)));
                    continue;
                }

                partitions.Add($"{signal.Specification}\u001f{signal.Type}");
            }
        }

        foreach ((string key, int count) in readyOccurrences)
        {
            if (count != 1)
            {
                failures.Add(Fatal(
                    "clustering-ready-ticket-cardinality",
                    $"Ready ticket occurs in {count} visited workgroups instead of exactly one.",
                    key,
                    sourcePaths.GetValueOrDefault(key)));
            }
        }

        return (
            visited,
            partitions.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private static bool RequireMetadata(
        string? value,
        string name,
        ManifestTicket ticket,
        ICollection<ReadinessFailure> failures)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        failures.Add(Waivable(
            $"self-{name}-missing",
            $"Self Jira hydration has no {name}.",
            ticket.Key,
            ticket.SourcePath));
        return false;
    }

    private static ReadinessFailure Fatal(
        string code,
        string message,
        string? ticketKey = null,
        string? sourcePath = null) =>
        new(code, message, ticketKey, sourcePath, Waivable: false);

    private static ReadinessFailure Waivable(
        string code,
        string message,
        string? ticketKey = null,
        string? sourcePath = null) =>
        new(code, message, ticketKey, sourcePath, Waivable: true);
}
