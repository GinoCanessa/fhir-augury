using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;

/// <summary>
/// The immutable receipt coordinate used to identify one member of the
/// publication corpus.
/// </summary>
public sealed record PreparedTicketPublicationCorpusItem(
    string TicketKey,
    string ReceiptId,
    string RunItemId,
    string ContributingRunId,
    string ItemKind,
    string ExpectedSourceRevision);

/// <summary>
/// A certified grouping partition and the canonical fingerprint of its
/// persisted semantic output.
/// </summary>
public sealed record PreparedTicketPublicationGroupingPartition(
    string PartitionKey,
    string OutputFingerprint);

public sealed record PreparedTicketPublicationUnaffectedFingerprint(
    string RunId,
    IReadOnlyList<string> ImpactedPartitionKeys,
    string AuthoredRowsFingerprint,
    string ReceiptCoordinatesFingerprint,
    string GroupingRowsFingerprint,
    string CombinedFingerprint,
    DateTimeOffset CapturedAt);

public sealed record PreparedTicketPublicationGroupingWorkItem(
    string RunId,
    string PartitionKey,
    string WorkGroupClean,
    string WorkGroupDisplay,
    string Specification,
    string Type,
    IReadOnlyList<string> RevisedTicketKeys,
    IReadOnlyList<string> TicketKeys,
    string OverlayCorpusFingerprint);

public sealed record PreparedTicketPublicationGroupingDelta(
    string RunId,
    string OverlayCorpusFingerprint,
    IReadOnlyList<PreparedTicketPublicationReconciliationGroupingImpact>
        Impacts,
    PreparedTicketPublicationUnaffectedFingerprint Unaffected,
    IReadOnlyList<PreparedTicketPublicationGroupingPartition>
        UnaffectedGroupingPartitions);

public sealed record PreparedTicketPublicationCandidateSnapshot(
    string RunId,
    string SnapshotId,
    string ProcessorKind,
    string TemporaryPath,
    string FinalPath,
    int SchemaVersion,
    long Sequence,
    long AuthoringEpoch,
    int ItemCount,
    int ReceiptCount,
    string Sha256,
    long SizeBytes,
    IReadOnlyDictionary<string, long> TableCounts,
    string OverlayCorpusFingerprint,
    string GroupingFingerprint,
    string GroupingImpactFingerprint,
    DateTimeOffset CreatedAt);

/// <summary>
/// Canonical publication-proof serialization shared by the Preparer and every
/// consumer of its review snapshots.
/// </summary>
public static class PreparedTicketPublicationContract
{
    public const int Version = 1;
    public const int CurrentVersion = Version;
    public const string PublicationRefreshPurpose = "publication-refresh";
    public const string PublicationReconciliationPurpose =
        "publication-reconciliation";
    public const string CanonicalEpochRecoveryPurpose =
        "canonical-epoch-recovery";
    public const string JiraSourceName = "jira";

    public static byte[] SerializeCanonicalEpochRecovery(
        string sourceRunId,
        long authoringEpoch,
        DateTimeOffset abandonedAt,
        IEnumerable<PreparedTicketPublicationCorpusItem> corpus,
        IEnumerable<PreparedTicketPublicationGroupingPartition> grouping,
        int contractVersion = CurrentVersion)
    {
        EnsureSupportedVersion(contractVersion);
        RequireValue(sourceRunId, nameof(sourceRunId));
        ArgumentOutOfRangeException.ThrowIfNegative(authoringEpoch);
        if (abandonedAt == default || abandonedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The canonical abandonment timestamp must be non-default UTC.",
                nameof(abandonedAt));
        }
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(grouping);

        PreparedTicketPublicationCorpusItem[] orderedCorpus = corpus
            .Select(ValidateCorpusItem)
            .OrderBy(item => item.TicketKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.TicketKey, StringComparer.Ordinal)
            .ThenBy(item => item.ReceiptId, StringComparer.Ordinal)
            .ThenBy(item => item.RunItemId, StringComparer.Ordinal)
            .ThenBy(item => item.ContributingRunId, StringComparer.Ordinal)
            .ThenBy(item => item.ItemKind, StringComparer.Ordinal)
            .ThenBy(item => item.ExpectedSourceRevision, StringComparer.Ordinal)
            .ToArray();
        PreparedTicketPublicationGroupingPartition[] orderedGrouping =
            grouping
                .OrderBy(
                    partition => partition.PartitionKey,
                    StringComparer.Ordinal)
                .ToArray();
        _ = ComputeCorpusFingerprint(orderedCorpus, contractVersion);
        _ = ComputeGroupingFingerprint(orderedGrouping, contractVersion);

        using MemoryStream output = new();
        using (Utf8JsonWriter writer = CreateWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", contractVersion);
            writer.WriteString("purpose", CanonicalEpochRecoveryPurpose);
            writer.WriteString("sourceRunId", sourceRunId);
            writer.WriteNumber("authoringEpoch", authoringEpoch);
            writer.WriteString("abandonedAt", abandonedAt);
            writer.WritePropertyName("corpus");
            using (JsonDocument document = JsonDocument.Parse(
                       SerializeCorpus(orderedCorpus, contractVersion)))
            {
                document.RootElement.GetProperty("corpus").WriteTo(writer);
            }
            writer.WritePropertyName("groupingFingerprints");
            using (JsonDocument document = JsonDocument.Parse(
                       SerializeGroupingFingerprints(
                           orderedGrouping,
                           contractVersion)))
            {
                document.RootElement.GetProperty("groupingFingerprints")
                    .WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    public static string ComputeCanonicalEpochRecoveryFingerprint(
        string sourceRunId,
        long authoringEpoch,
        DateTimeOffset abandonedAt,
        IEnumerable<PreparedTicketPublicationCorpusItem> corpus,
        IEnumerable<PreparedTicketPublicationGroupingPartition> grouping,
        int contractVersion = CurrentVersion)
        => ComputeSha256(
            SerializeCanonicalEpochRecovery(
                sourceRunId,
                authoringEpoch,
                abandonedAt,
                corpus,
                grouping,
                contractVersion));

    public static string ComputeCorpusFingerprint(
        IEnumerable<PreparedTicketPublicationCorpusItem> items,
        int contractVersion = CurrentVersion)
        => ComputeSha256(SerializeCorpus(items, contractVersion));

    public static string ComputePublicationRefreshInputFingerprint(
        string sourceRunId,
        IEnumerable<PreparedTicketPublicationCorpusItem> items,
        int contractVersion = CurrentVersion)
        => ComputeSha256(
            SerializePublicationRefreshInput(
                sourceRunId,
                items,
                contractVersion));

    public static string ComputeRefreshInputFingerprint(
        string sourceRunId,
        IEnumerable<PreparedTicketPublicationCorpusItem> items,
        int contractVersion = CurrentVersion)
        => ComputePublicationRefreshInputFingerprint(
            sourceRunId,
            items,
            contractVersion);

    public static string ComputeGroupingPartitionFingerprint(
        PreparedTicketGroupingPayload partition,
        int contractVersion = CurrentVersion)
    {
        ArgumentNullException.ThrowIfNull(partition);
        return ComputeGroupingFingerprint([partition], contractVersion);
    }

    public static string ComputeGroupingOutputFingerprint(
        PreparedTicketGroupingPayload partition,
        int contractVersion = CurrentVersion)
        => ComputeGroupingPartitionFingerprint(partition, contractVersion);

    public static string ComputeGroupingFingerprint(
        IEnumerable<PreparedTicketGroupingPayload> partitions,
        int contractVersion = CurrentVersion)
        => ComputeSha256(SerializeGrouping(partitions, contractVersion));

    public static string ComputeGroupingFingerprint(
        IEnumerable<PreparedTicketPublicationGroupingPartition> partitions,
        int contractVersion = CurrentVersion)
        => ComputeSha256(
            SerializeGroupingFingerprints(partitions, contractVersion));

    public static string ComputeGroupingImpactFingerprint(
        IEnumerable<PreparedTicketPublicationReconciliationGroupingImpact>
            impacts,
        int contractVersion = CurrentVersion)
    {
        EnsureSupportedVersion(contractVersion);
        PreparedTicketPublicationReconciliationGroupingImpact[] ordered =
            ValidateGroupingImpacts(impacts);
        return ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(new
        {
            contractVersion,
            purpose = PublicationReconciliationPurpose,
            impacts = ordered.Select(impact => new
            {
                impact.PartitionKey,
                revisedTicketKeys = impact.RevisedTicketKeys
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(value => value, StringComparer.Ordinal),
                impact.BaselineCorpusFingerprint,
                impact.BaselineOutputFingerprint,
                impact.BaselineProtectedRowsFingerprint,
                impact.StagedCorpusFingerprint,
                impact.StagedOutputFingerprint,
                impact.StagedProtectedRowsFingerprint,
            }),
        }));
    }

    private static PreparedTicketPublicationReconciliationGroupingImpact[]
        ValidateGroupingImpacts(
            IEnumerable<PreparedTicketPublicationReconciliationGroupingImpact>
                impacts)
    {
        ArgumentNullException.ThrowIfNull(impacts);
        PreparedTicketPublicationReconciliationGroupingImpact[] ordered =
            impacts.OrderBy(impact => impact.PartitionKey, StringComparer.Ordinal)
                .ToArray();
        if (ordered.Any(impact =>
                string.IsNullOrWhiteSpace(impact.PartitionKey) ||
                !impact.Complete ||
                string.IsNullOrWhiteSpace(impact.StagedCorpusFingerprint) ||
                string.IsNullOrWhiteSpace(impact.StagedOutputFingerprint) ||
                string.IsNullOrWhiteSpace(
                    impact.StagedProtectedRowsFingerprint)) ||
            ordered.Select(impact => impact.PartitionKey)
                .Distinct(StringComparer.Ordinal).Count() != ordered.Length)
        {
            throw new ArgumentException(
                "Grouping impacts must be complete and have unique partition coordinates.",
                nameof(impacts));
        }
        foreach (PreparedTicketPublicationReconciliationGroupingImpact impact
                 in ordered)
        {
            RequireSha256(
                impact.BaselineCorpusFingerprint,
                nameof(impact.BaselineCorpusFingerprint));
            RequireSha256(
                impact.BaselineOutputFingerprint,
                nameof(impact.BaselineOutputFingerprint));
            RequireSha256(
                impact.BaselineProtectedRowsFingerprint,
                nameof(impact.BaselineProtectedRowsFingerprint));
            RequireSha256(
                impact.StagedCorpusFingerprint,
                nameof(impact.StagedCorpusFingerprint));
            RequireSha256(
                impact.StagedOutputFingerprint,
                nameof(impact.StagedOutputFingerprint));
            RequireSha256(
                impact.StagedProtectedRowsFingerprint,
                nameof(impact.StagedProtectedRowsFingerprint));
            if (impact.RevisedTicketKeys
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                impact.RevisedTicketKeys.Count)
            {
                throw new ArgumentException(
                    "Grouping impacts cannot contain duplicate revised tickets.",
                    nameof(impacts));
            }
        }
        return ordered;
    }

    public static string ComputeReconciliationGroupingFingerprint(
        IEnumerable<PreparedTicketPublicationGroupingPartition> unaffected,
        IEnumerable<PreparedTicketPublicationReconciliationGroupingImpact>
            impacts,
        int contractVersion = CurrentVersion)
    {
        EnsureSupportedVersion(contractVersion);
        ArgumentNullException.ThrowIfNull(unaffected);
        PreparedTicketPublicationReconciliationGroupingImpact[] complete =
            ValidateGroupingImpacts(impacts);
        // Zero-member replacements remove partitions, but remain in the impact audit.
        string emptyCorpusFingerprint = ComputeSha256([]);
        PreparedTicketPublicationGroupingPartition[] replacements = complete
            .Where(impact => !string.Equals(
                impact.StagedCorpusFingerprint,
                emptyCorpusFingerprint,
                StringComparison.Ordinal))
            .Select(impact => new PreparedTicketPublicationGroupingPartition(
                impact.PartitionKey,
                impact.StagedOutputFingerprint
                ?? throw new ArgumentException(
                    "A reconciliation grouping impact is incomplete.",
                    nameof(impacts))))
            .ToArray();
        return ComputeGroupingFingerprint(
            unaffected.Concat(replacements),
            contractVersion);
    }

    public static byte[] SerializeCorpus(
        IEnumerable<PreparedTicketPublicationCorpusItem> items,
        int contractVersion = CurrentVersion)
    {
        EnsureSupportedVersion(contractVersion);
        ArgumentNullException.ThrowIfNull(items);

        PreparedTicketPublicationCorpusItem[] ordered = items
            .Select(ValidateCorpusItem)
            .OrderBy(item => item.TicketKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.TicketKey, StringComparer.Ordinal)
            .ThenBy(item => item.ReceiptId, StringComparer.Ordinal)
            .ThenBy(item => item.RunItemId, StringComparer.Ordinal)
            .ThenBy(item => item.ContributingRunId, StringComparer.Ordinal)
            .ThenBy(item => item.ItemKind, StringComparer.Ordinal)
            .ThenBy(item => item.ExpectedSourceRevision, StringComparer.Ordinal)
            .ToArray();
        if (ordered
            .Select(item => item.TicketKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != ordered.Length)
        {
            throw new ArgumentException(
                "The publication corpus contains more than one receipt coordinate for a ticket.",
                nameof(items));
        }

        using MemoryStream output = new();
        using (Utf8JsonWriter writer = CreateWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", contractVersion);
            writer.WriteStartArray("corpus");
            foreach (PreparedTicketPublicationCorpusItem item in ordered)
            {
                writer.WriteStartObject();
                writer.WriteString("ticketKey", item.TicketKey);
                writer.WriteString("receiptId", item.ReceiptId);
                writer.WriteString("runItemId", item.RunItemId);
                writer.WriteString(
                    "contributingRunId",
                    item.ContributingRunId);
                writer.WriteString("itemKind", item.ItemKind);
                writer.WriteString(
                    "expectedSourceRevision",
                    item.ExpectedSourceRevision);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    public static byte[] SerializePublicationRefreshInput(
        string sourceRunId,
        IEnumerable<PreparedTicketPublicationCorpusItem> items,
        int contractVersion = CurrentVersion)
    {
        EnsureSupportedVersion(contractVersion);
        RequireValue(sourceRunId, nameof(sourceRunId));
        ArgumentNullException.ThrowIfNull(items);
        string corpusFingerprint = ComputeCorpusFingerprint(
            items,
            contractVersion);

        using MemoryStream output = new();
        using (Utf8JsonWriter writer = CreateWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", contractVersion);
            writer.WriteString("purpose", PublicationRefreshPurpose);
            writer.WriteString("sourceRunId", sourceRunId);
            writer.WriteString("corpusFingerprint", corpusFingerprint);
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    public static byte[] SerializeGrouping(
        IEnumerable<PreparedTicketGroupingPayload> partitions,
        int contractVersion = CurrentVersion)
    {
        EnsureSupportedVersion(contractVersion);
        ArgumentNullException.ThrowIfNull(partitions);

        PreparedTicketGroupingPayload[] ordered = partitions
            .Select(ValidatePartition)
            .OrderBy(
                partition => partition.WorkGroupClean,
                StringComparer.Ordinal)
            .ThenBy(
                partition => partition.Specification,
                StringComparer.Ordinal)
            .ThenBy(partition => partition.Type, StringComparer.Ordinal)
            .ThenBy(
                partition => partition.WorkGroupDisplay,
                StringComparer.Ordinal)
            .ToArray();
        if (ordered
            .Select(PartitionCoordinate)
            .Distinct(StringComparer.Ordinal)
            .Count() != ordered.Length)
        {
            throw new ArgumentException(
                "The publication grouping contains a duplicate partition.",
                nameof(partitions));
        }

        using MemoryStream output = new();
        using (Utf8JsonWriter writer = CreateWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", contractVersion);
            writer.WriteStartArray("partitions");
            foreach (PreparedTicketGroupingPayload partition in ordered)
            {
                WritePartition(writer, partition);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    public static byte[] SerializeGroupingFingerprints(
        IEnumerable<PreparedTicketPublicationGroupingPartition> partitions,
        int contractVersion = CurrentVersion)
    {
        EnsureSupportedVersion(contractVersion);
        ArgumentNullException.ThrowIfNull(partitions);
        PreparedTicketPublicationGroupingPartition[] ordered = partitions
            .Select(partition =>
            {
                ArgumentNullException.ThrowIfNull(partition);
                RequireValue(
                    partition.PartitionKey,
                    nameof(partition.PartitionKey));
                RequireSha256(
                    partition.OutputFingerprint,
                    nameof(partition.OutputFingerprint));
                return partition;
            })
            .OrderBy(
                partition => partition.PartitionKey,
                StringComparer.Ordinal)
            .ToArray();
        if (ordered
            .Select(partition => partition.PartitionKey)
            .Distinct(StringComparer.Ordinal)
            .Count() != ordered.Length)
        {
            throw new ArgumentException(
                "The publication grouping contains a duplicate partition coordinate.",
                nameof(partitions));
        }

        using MemoryStream output = new();
        using (Utf8JsonWriter writer = CreateWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("contractVersion", contractVersion);
            writer.WriteStartArray("groupingFingerprints");
            foreach (PreparedTicketPublicationGroupingPartition partition in
                     ordered)
            {
                writer.WriteStartObject();
                writer.WriteString("partitionKey", partition.PartitionKey);
                writer.WriteString(
                    "outputFingerprint",
                    partition.OutputFingerprint);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    public static void EnsureSupportedVersion(int contractVersion)
    {
        if (contractVersion != CurrentVersion)
        {
            throw new NotSupportedException(
                $"Unsupported prepared-ticket publication contract version {contractVersion}.");
        }
    }

    private static PreparedTicketPublicationCorpusItem ValidateCorpusItem(
        PreparedTicketPublicationCorpusItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        RequireValue(item.TicketKey, nameof(item.TicketKey));
        RequireValue(item.ReceiptId, nameof(item.ReceiptId));
        RequireValue(item.RunItemId, nameof(item.RunItemId));
        RequireValue(
            item.ContributingRunId,
            nameof(item.ContributingRunId));
        RequireValue(item.ItemKind, nameof(item.ItemKind));
        RequireValue(
            item.ExpectedSourceRevision,
            nameof(item.ExpectedSourceRevision));
        return item;
    }

    private static PreparedTicketGroupingPayload ValidatePartition(
        PreparedTicketGroupingPayload partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        RequireValue(
            partition.WorkGroupClean,
            nameof(partition.WorkGroupClean));
        RequireValue(
            partition.WorkGroupDisplay,
            nameof(partition.WorkGroupDisplay));
        RequireValue(
            partition.Specification,
            nameof(partition.Specification));
        RequireValue(partition.Type, nameof(partition.Type));
        ArgumentNullException.ThrowIfNull(partition.Topics);
        foreach (PreparedTicketTopicPayload topic in partition.Topics)
        {
            ArgumentNullException.ThrowIfNull(topic);
            RequireValue(
                topic.ShortDescription,
                nameof(topic.ShortDescription));
            ArgumentNullException.ThrowIfNull(topic.LinkedTicketGroups);
            ArgumentNullException.ThrowIfNull(topic.RemainingTicketKeys);
            foreach (PreparedTicketTopicGroupPayload group in
                     topic.LinkedTicketGroups)
            {
                ArgumentNullException.ThrowIfNull(group);
                RequireValue(
                    group.FirstTicketKey,
                    nameof(group.FirstTicketKey));
                RequireValue(group.Rationale, nameof(group.Rationale));
                ArgumentNullException.ThrowIfNull(group.Members);
                foreach (PreparedTicketTopicGroupMemberPayload member in
                         group.Members)
                {
                    ArgumentNullException.ThrowIfNull(member);
                    RequireValue(
                        member.TicketKey,
                        nameof(member.TicketKey));
                }
            }
            foreach (string ticketKey in topic.RemainingTicketKeys)
            {
                RequireValue(ticketKey, "RemainingTicketKey");
            }
        }
        return partition;
    }

    private static void WritePartition(
        Utf8JsonWriter writer,
        PreparedTicketGroupingPayload partition)
    {
        writer.WriteStartObject();
        writer.WriteString("workGroupClean", partition.WorkGroupClean);
        writer.WriteString("workGroupDisplay", partition.WorkGroupDisplay);
        writer.WriteString("specification", partition.Specification);
        writer.WriteString("type", partition.Type);
        writer.WriteStartArray("topics");
        foreach (PreparedTicketTopicPayload topic in partition.Topics)
        {
            writer.WriteStartObject();
            writer.WriteString("shortDescription", topic.ShortDescription);
            WriteNullableString(
                writer,
                "longerDescription",
                topic.LongerDescription);
            if (topic.RenderOrderHint is int renderOrderHint)
            {
                writer.WriteNumber("renderOrderHint", renderOrderHint);
            }
            else
            {
                writer.WriteNull("renderOrderHint");
            }

            writer.WriteStartArray("linkedTicketGroups");
            for (int groupIndex = 0;
                 groupIndex < topic.LinkedTicketGroups.Count;
                 groupIndex++)
            {
                PreparedTicketTopicGroupPayload group =
                    topic.LinkedTicketGroups[groupIndex];
                writer.WriteStartObject();
                writer.WriteNumber("orderInTopic", groupIndex);
                writer.WriteString("firstTicketKey", group.FirstTicketKey);
                writer.WriteString("rationale", group.Rationale);
                writer.WriteStartArray("members");
                foreach (PreparedTicketTopicGroupMemberPayload member in
                         group.Members
                             .OrderBy(value => value.Order)
                             .ThenBy(
                                 value => value.TicketKey,
                                 StringComparer.OrdinalIgnoreCase)
                             .ThenBy(
                                 value => value.TicketKey,
                                 StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("ticketKey", member.TicketKey);
                    writer.WriteNumber("order", member.Order);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("remainingTicketKeys");
            for (int ticketIndex = 0;
                 ticketIndex < topic.RemainingTicketKeys.Count;
                 ticketIndex++)
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "ticketKey",
                    topic.RemainingTicketKeys[ticketIndex]);
                writer.WriteNumber("order", ticketIndex);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static Utf8JsonWriter CreateWriter(Stream output)
        => new(
            output,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false,
            });

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static string PartitionCoordinate(
        PreparedTicketGroupingPayload partition)
        => string.Join(
            "\0",
            partition.WorkGroupClean,
            partition.Specification,
            partition.Type);

    private static void RequireValue(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Publication fingerprint coordinates cannot be null or whitespace.",
                parameterName);
        }
    }

    internal static void RequireSha256(string? value, string parameterName)
    {
        RequireValue(value, parameterName);
        if (value!.Length != 64 ||
            value.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Publication fingerprints must be canonical lower-case SHA-256 values.",
                parameterName);
        }
    }

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
