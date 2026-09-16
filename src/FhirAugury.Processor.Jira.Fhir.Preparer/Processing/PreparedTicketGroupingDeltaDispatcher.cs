using FhirAugury.Common.WorkGroups;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

/// <summary>
/// Computes and stages complete grouping-partition replacements for a
/// publication reconciliation. Canonical grouping is never mutated here.
/// </summary>
public sealed class PreparedTicketGroupingDeltaDispatcher(
    PreparerDatabase database)
{
    public Task<PreparedTicketPublicationGroupingDelta> PrepareAsync(
        string runId,
        CancellationToken ct = default)
        => database.PreparePublicationReconciliationGroupingDeltaAsync(
            runId,
            ct);

    public async Task<PreparedTicketPublicationReconciliationProof>
        CreateProofAsync(
            string runId,
            DateTimeOffset? capturedAt = null,
            CancellationToken ct = default)
    {
        PreparedTicketPublicationReconciliationComparison comparison =
            await database.GetPublicationReconciliationComparisonAsync(
                runId,
                ct)
            ?? throw new KeyNotFoundException(
                $"Publication reconciliation '{runId}' was not found.");
        PreparedTicketPublicationGroupingDelta delta =
            await PrepareAsync(runId, ct);
        if (delta.Impacts.Any(impact => !impact.Complete))
        {
            throw new InvalidOperationException(
                "The complete grouping-impact closure has not been staged.");
        }
        await database.ValidatePublicationReconciliationUnaffectedAsync(
            runId,
            ct);
        return new(
            PreparedTicketPublicationReconciliationContract.CurrentVersion,
            PreparedTicketPublicationContract
                .PublicationReconciliationPurpose,
            comparison.SourceRunId,
            comparison.SourceSnapshotId,
            comparison.StableJiraGeneration,
            comparison.Items.Count,
            comparison.Items.Count(item => item.Disposition ==
                PreparedTicketPublicationReconciliationDispositionValues
                    .CarryForward),
            comparison.Items.Count(item => item.Disposition ==
                PreparedTicketPublicationReconciliationDispositionValues
                    .ReAuthor),
            delta.OverlayCorpusFingerprint,
            PreparedTicketPublicationContract
                .ComputeGroupingImpactFingerprint(delta.Impacts),
            (capturedAt ?? DateTimeOffset.UtcNow).ToUniversalTime());
    }

    public async Task<PreparedTicketPublicationGroupingDelta> DispatchAsync(
        string runId,
        Func<PreparedTicketPublicationGroupingWorkItem,
            CancellationToken,
            Task<PreparedTicketGroupingPayload>> replacePartition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacePartition);
        PreparedTicketPublicationGroupingDelta delta =
            await PrepareAsync(runId, ct);
        PreparedTicketPublicationCorpusOverlay overlay =
            await database.GetPublicationReconciliationCorpusAsync(runId, ct);
        Dictionary<string, Partition> partitions =
            BuildOverlayPartitions(overlay);
        List<PreparedTicketGroupingPayload> replacements =
            new(delta.Impacts.Count);
        foreach (PreparedTicketPublicationReconciliationGroupingImpact impact
                 in delta.Impacts)
        {
            Partition partition = partitions.TryGetValue(
                impact.PartitionKey,
                out Partition? current)
                ? current
                : ParseEmptyPartition(impact.PartitionKey);
            PreparedTicketGroupingPayload replacement;
            if (partition.TicketKeys.Count == 0)
            {
                replacement = partition.EmptyPayload();
            }
            else
            {
                replacement = await replacePartition(
                    new PreparedTicketPublicationGroupingWorkItem(
                        runId,
                        impact.PartitionKey,
                        partition.WorkGroupClean,
                        partition.WorkGroupDisplay,
                        partition.Specification,
                        partition.Type,
                        impact.RevisedTicketKeys,
                        partition.TicketKeys,
                        overlay.CorpusFingerprint),
                    ct);
            }
            replacements.Add(replacement);
        }
        await StageReplacementsAsync(runId, replacements, ct);
        return await PrepareAsync(runId, ct);
    }

    public async Task StageReplacementsAsync(
        string runId,
        IReadOnlyList<PreparedTicketGroupingPayload> replacements,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        PreparedTicketPublicationGroupingDelta delta =
            await PrepareAsync(runId, ct);
        PreparedTicketPublicationCorpusOverlay overlay =
            await database.GetPublicationReconciliationCorpusAsync(runId, ct);
        Dictionary<string, Partition> partitions =
            BuildOverlayPartitions(overlay);
        Dictionary<string, PreparedTicketGroupingPayload> supplied = [];
        foreach (PreparedTicketGroupingPayload replacement in replacements)
        {
            PreparedTicketGroupingPayloadValidator.ThrowIfInvalid(replacement);
            string partitionKey = PreparerDatabase.GetPartitionKey(
                replacement.WorkGroupClean,
                replacement.Specification,
                replacement.Type);
            if (!supplied.TryAdd(partitionKey, replacement))
            {
                throw new ArgumentException(
                    $"More than one replacement was supplied for grouping partition '{partitionKey}'.",
                    nameof(replacements));
            }
        }
        string[] expected = delta.Impacts.Select(value => value.PartitionKey)
            .Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(
                supplied.Keys.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new PreparedTicketPublicationReconciliationException(
                PreparedTicketPublicationReconciliationFailureCodes
                    .GroupingImpactMismatch,
                "Grouping replacements must exactly cover the impacted partition closure.");
        }

        List<PreparedTicketPublicationStagedGroupingReplacement> staged = [];
        foreach (PreparedTicketPublicationReconciliationGroupingImpact impact
                 in delta.Impacts)
        {
            PreparedTicketGroupingPayload replacement =
                supplied[impact.PartitionKey];
            Partition partition = partitions.TryGetValue(
                impact.PartitionKey,
                out Partition? current)
                ? current
                : ParseEmptyPartition(impact.PartitionKey);
            HashSet<string> members = partition.TicketKeys.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            string[] unknown = ReferencedTicketKeys(replacement)
                .Where(key => !members.Contains(key)).ToArray();
            if (unknown.Length != 0)
            {
                throw new PreparedTicketPublicationReconciliationException(
                    PreparedTicketPublicationReconciliationFailureCodes
                        .GroupingImpactMismatch,
                    $"Grouping partition '{impact.PartitionKey}' references tickets outside its overlay membership: {string.Join(", ", unknown)}.");
            }
            if (members.Count == 0 && replacement.Topics.Count != 0)
            {
                throw new PreparedTicketPublicationReconciliationException(
                    PreparedTicketPublicationReconciliationFailureCodes
                        .GroupingImpactMismatch,
                    $"Empty grouping partition '{impact.PartitionKey}' cannot retain topics.");
            }
            staged.Add(new(
                runId,
                impact.PartitionKey,
                System.Text.Json.JsonSerializer.Serialize(replacement),
                ComputePartitionCorpusFingerprint(partition.TicketKeys),
                PreparedTicketPublicationContract
                    .ComputeGroupingOutputFingerprint(replacement),
                impact.BaselineProtectedRowsFingerprint,
                DateTimeOffset.UtcNow));
        }
        await database.SaveCompletePublicationReconciliationGroupingAsync(
            runId,
            staged,
            ct);
    }

    private static Dictionary<string, Partition> BuildOverlayPartitions(
        PreparedTicketPublicationCorpusOverlay overlay)
        => overlay.Tickets
            .Select(ticket =>
            {
                PreparedJiraHydrationRow? self = ticket.Hydration.JiraRows
                    .SingleOrDefault(row => string.Equals(
                        row.JiraKey,
                        ticket.TicketKey,
                        StringComparison.OrdinalIgnoreCase));
                if (self is null || string.IsNullOrWhiteSpace(self.Type))
                {
                    return null;
                }
                string workGroupClean =
                    Hl7WorkGroupNameCleaner.Clean(self.WorkGroup);
                if (string.IsNullOrWhiteSpace(workGroupClean))
                {
                    return null;
                }
                string specification = string.IsNullOrWhiteSpace(
                    self.Specification)
                    ? "Unspecified"
                    : self.Specification.Trim();
                string type = self.Type.Trim();
                return new
                {
                    Key = PreparerDatabase.GetPartitionKey(
                        workGroupClean,
                        specification,
                        type),
                    WorkGroupClean = workGroupClean,
                    WorkGroupDisplay = string.IsNullOrWhiteSpace(self.WorkGroup)
                        ? workGroupClean
                        : self.WorkGroup.Trim(),
                    Specification = specification,
                    Type = type,
                    ticket.TicketKey,
                };
            })
            .Where(value => value is not null)
            .GroupBy(value => value!.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new Partition(
                    group.First()!.WorkGroupClean,
                    group.First()!.WorkGroupDisplay,
                    group.First()!.Specification,
                    group.First()!.Type,
                    group.Select(value => value!.TicketKey)
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(value => value, StringComparer.Ordinal)
                        .ToArray()),
                StringComparer.Ordinal);

    private static Partition ParseEmptyPartition(string partitionKey)
    {
        string[] coordinates = partitionKey.Split('\u001f');
        if (coordinates.Length != 3 ||
            coordinates.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                $"Grouping partition key '{partitionKey}' is invalid.");
        }
        return new(
            coordinates[0],
            coordinates[0],
            coordinates[1],
            coordinates[2],
            []);
    }

    private static IEnumerable<string> ReferencedTicketKeys(
        PreparedTicketGroupingPayload payload)
        => payload.Topics.SelectMany(topic =>
            topic.LinkedTicketGroups.SelectMany(group =>
                    group.Members.Select(member => member.TicketKey))
                .Concat(topic.RemainingTicketKeys));

    internal static string ComputePartitionCorpusFingerprint(
        IEnumerable<string> ticketKeys)
        => FhirAugury.Processing.Common.Authoring.AuthoringResultHasher
            .HashNormalizedUtf8(string.Join(
                "\n",
                ticketKeys.OrderBy(
                        value => value,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(value => value, StringComparer.Ordinal)));

    private sealed record Partition(
        string WorkGroupClean,
        string WorkGroupDisplay,
        string Specification,
        string Type,
        IReadOnlyList<string> TicketKeys)
    {
        public PreparedTicketGroupingPayload EmptyPayload() => new()
        {
            WorkGroupClean = WorkGroupClean,
            WorkGroupDisplay = WorkGroupDisplay,
            Specification = Specification,
            Type = Type,
            Topics = [],
        };
    }
}
