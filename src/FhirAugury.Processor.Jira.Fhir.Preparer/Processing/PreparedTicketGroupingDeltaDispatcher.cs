using FhirAugury.Common.WorkGroups;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Contracts;
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
                        delta.OverlayCorpusFingerprint),
                    ct);
            }
            replacements.Add(replacement);
        }
        await StageReplacementsAsync(runId, replacements, ct);
        return await PrepareAsync(runId, ct);
    }

    public static PreparedTicketGroupingStageContext CreateStageContext(
        PreparedTicketPublicationGroupingWorkItem workItem,
        AuthoringRunStageLease lease)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(lease);
        return new(
            workItem.RunId,
            lease.StageId,
            lease.LeaseId,
            workItem.OverlayCorpusFingerprint,
            workItem.PartitionKey,
            workItem.OverlayCorpusFingerprint,
            workItem.RevisedTicketKeys.ToArray(),
            workItem.TicketKeys.ToArray());
    }

    public async Task<PreparedTicketPublicationGroupingWorkItem>
        GetStageWorkItemAsync(
            string runId,
            string stageId,
            string stageLeaseId,
            string partitionKey,
            string inputFingerprint,
            CancellationToken ct = default)
    {
        PreparedTicketGroupingStageContext context = new(
            runId,
            stageId,
            stageLeaseId,
            inputFingerprint,
            partitionKey,
            inputFingerprint,
            [],
            []);
        await database.ValidatePublicationReconciliationGroupingStageAsync(
            context,
            ct);

        PreparedTicketPublicationGroupingDelta delta =
            await PrepareAsync(runId, ct);
        if (!string.Equals(
                inputFingerprint,
                delta.OverlayCorpusFingerprint,
                StringComparison.Ordinal))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                $"Reconciliation grouping stage '{stageId}' does not match the candidate overlay fingerprint.");
        }
        PreparedTicketPublicationReconciliationGroupingImpact impact =
            delta.Impacts.SingleOrDefault(value => string.Equals(
                value.PartitionKey,
                partitionKey,
                StringComparison.Ordinal))
            ?? throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                $"Grouping partition '{partitionKey}' is not in reconciliation '{runId}'.");
        PreparedTicketPublicationCorpusOverlay overlay =
            await database.GetPublicationReconciliationCorpusAsync(runId, ct);
        Dictionary<string, Partition> partitions =
            BuildOverlayPartitions(overlay);
        Partition partition = partitions.TryGetValue(
            impact.PartitionKey,
            out Partition? current)
            ? current
            : ParseEmptyPartition(impact.PartitionKey);
        return CreateWorkItem(
            runId,
            delta.OverlayCorpusFingerprint,
            impact,
            partition);
    }

    public async Task<PreparedTicketPublicationGroupingWorkItem>
        GetStageWorkItemAsync(
            PreparedTicketGroupingStageContext context,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.HasCompleteReconciliationContext())
        {
            throw new ArgumentException(
                "Complete reconciliation grouping stage context is required.",
                nameof(context));
        }
        PreparedTicketPublicationGroupingWorkItem workItem =
            await GetStageWorkItemAsync(
                context.RunId,
                context.StageId,
                context.StageLeaseId,
                context.PartitionKey!,
                context.InputFingerprint,
                ct);
        if (!string.Equals(
                context.OverlayCorpusFingerprint,
                workItem.OverlayCorpusFingerprint,
                StringComparison.Ordinal) ||
            !context.RevisedTicketKeys!.SequenceEqual(
                workItem.RevisedTicketKeys,
                StringComparer.Ordinal) ||
            !context.TicketKeys!.SequenceEqual(
                workItem.TicketKeys,
                StringComparer.Ordinal))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.StageFingerprintMismatch,
                $"Reconciliation grouping stage '{context.StageId}' does not match the exact impacted overlay partition.");
        }
        return workItem;
    }

    public async Task<AuthoringRunStageReceipt> DispatchStageAsync(
        string runId,
        string partitionKey,
        string inputFingerprint,
        AuthoringRunStageLease lease,
        Func<PreparedTicketPublicationGroupingWorkItem,
            AuthoringRunStageLease,
            CancellationToken,
            Task> replacePartition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(replacePartition);
        PreparedTicketPublicationGroupingWorkItem workItem =
            await GetStageWorkItemAsync(
                runId,
                lease.StageId,
                lease.LeaseId,
                partitionKey,
                inputFingerprint,
                ct);
        PreparedTicketGroupingStageContext context =
            CreateStageContext(workItem, lease);
        if (workItem.TicketKeys.Count == 0)
        {
            _ = await StageReplacementsAsync(
                ParseEmptyPartition(partitionKey).EmptyPayload(),
                context,
                ct);
        }
        else
        {
            await replacePartition(workItem, lease, ct);
        }

        return await RequireStageReceiptAsync(
            runId,
            lease.StageId,
            lease.LeaseId,
            partitionKey,
            inputFingerprint,
            expectEmpty: workItem.TicketKeys.Count == 0,
            ct: ct);
    }

    public async Task<(
        PreparedTicketGroupingSaveResult Result,
        AuthoringRunStageReceipt Receipt)> StageReplacementsAsync(
            PreparedTicketGroupingPayload replacement,
            PreparedTicketGroupingStageContext context,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(context);
        PreparedTicketGroupingPayloadValidator.ThrowIfInvalid(replacement);
        PreparedTicketPublicationGroupingWorkItem workItem =
            await GetStageWorkItemAsync(context, ct);
        string replacementPartitionKey = PreparerDatabase.GetPartitionKey(
            replacement.WorkGroupClean,
            replacement.Specification,
            replacement.Type);
        if (!string.Equals(
                replacementPartitionKey,
                workItem.PartitionKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                replacement.WorkGroupDisplay,
                workItem.WorkGroupDisplay,
                StringComparison.Ordinal))
        {
            throw new PreparedTicketPublicationReconciliationException(
                PreparedTicketPublicationReconciliationFailureCodes
                    .GroupingImpactMismatch,
                "The grouping replacement does not match its exact overlay partition.");
        }

        HashSet<string> members = workItem.TicketKeys.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        string[] unknown = ReferencedTicketKeys(replacement)
            .Where(key => !members.Contains(key))
            .ToArray();
        if (unknown.Length != 0)
        {
            throw new PreparedTicketPublicationReconciliationException(
                PreparedTicketPublicationReconciliationFailureCodes
                    .GroupingImpactMismatch,
                $"Grouping partition '{workItem.PartitionKey}' references tickets outside its overlay membership: {string.Join(", ", unknown)}.");
        }
        if (members.Count == 0 && replacement.Topics.Count != 0)
        {
            throw new PreparedTicketPublicationReconciliationException(
                PreparedTicketPublicationReconciliationFailureCodes
                    .GroupingImpactMismatch,
                $"Empty grouping partition '{workItem.PartitionKey}' cannot retain topics.");
        }

        PreparedTicketPublicationGroupingDelta delta =
            await PrepareAsync(context.RunId, ct);
        PreparedTicketPublicationReconciliationGroupingImpact impact =
            delta.Impacts.Single(value => string.Equals(
                value.PartitionKey,
                workItem.PartitionKey,
                StringComparison.Ordinal));
        PreparedTicketGroupingSaveResult result = CountRows(replacement);
        PreparedTicketPublicationStagedGroupingReplacement staged = new(
            context.RunId,
            workItem.PartitionKey,
            System.Text.Json.JsonSerializer.Serialize(replacement),
            ComputePartitionCorpusFingerprint(workItem.TicketKeys),
            PreparedTicketPublicationContract
                .ComputeGroupingOutputFingerprint(replacement),
            impact.BaselineProtectedRowsFingerprint,
            DateTimeOffset.UtcNow);
        AuthoringRunStageReceipt receipt =
            await database.SavePublicationReconciliationGroupingStageAsync(
                context,
                staged,
                ct);
        if (receipt.TopicRows != result.TopicRows ||
            receipt.TopicGroupRows != result.TopicGroupRows ||
            receipt.MemberRows != result.MemberRows)
        {
            throw new InvalidOperationException(
                $"Grouping stage receipt for partition '{workItem.PartitionKey}' does not match the submitted replacement.");
        }
        return (result, receipt);
    }

    public async Task<AuthoringRunStageReceipt> RequireStageReceiptAsync(
        string runId,
        string stageId,
        string? stageLeaseId,
        string partitionKey,
        string inputFingerprint,
        bool expectEmpty = false,
        CancellationToken ct = default)
    {
        AuthoringRunStageReceipt receipt =
            await database
                .GetPublicationReconciliationGroupingStageReceiptAsync(
                    runId,
                    stageId,
                    partitionKey,
                    inputFingerprint,
                    stageLeaseId,
                    ct)
            ?? throw new InvalidOperationException(
                $"Reconciliation grouping partition '{partitionKey}' completed without a matching durable staged receipt.");
        if (expectEmpty &&
            (receipt.TopicRows != 0 ||
             receipt.TopicGroupRows != 0 ||
             receipt.MemberRows != 0))
        {
            throw new InvalidOperationException(
                $"Empty reconciliation grouping partition '{partitionKey}' has a non-empty staged receipt.");
        }
        return receipt;
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

    private static PreparedTicketPublicationGroupingWorkItem CreateWorkItem(
        string runId,
        string overlayCorpusFingerprint,
        PreparedTicketPublicationReconciliationGroupingImpact impact,
        Partition partition)
        => new(
            runId,
            impact.PartitionKey,
            partition.WorkGroupClean,
            partition.WorkGroupDisplay,
            partition.Specification,
            partition.Type,
            impact.RevisedTicketKeys,
            partition.TicketKeys,
            overlayCorpusFingerprint);

    private static PreparedTicketGroupingSaveResult CountRows(
        PreparedTicketGroupingPayload payload)
        => new(
            payload.WorkGroupClean,
            payload.Specification,
            payload.Type,
            payload.Topics.Count,
            payload.Topics.Sum(topic =>
                topic.LinkedTicketGroups.Count),
            payload.Topics.Sum(topic =>
                topic.LinkedTicketGroups.Sum(group =>
                    group.Members.Count) +
                topic.RemainingTicketKeys.Count));

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
