using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;

public sealed record PreparedTicketPublicationProtectedValue(
    string Column,
    string StorageClass,
    string? Value);

public sealed record PreparedTicketPublicationProtectedRow(
    string Table,
    string Scope,
    string Key,
    IReadOnlyList<PreparedTicketPublicationProtectedValue> Values);

public sealed record PreparedTicketPublicationProtectedRowFingerprint(
    string Table,
    string Scope,
    string Key,
    string Fingerprint);

public sealed record PreparedTicketPublicationProtectedGroupingFingerprint(
    string PartitionKey,
    string CorpusFingerprint,
    string OutputFingerprint,
    string ProtectedRowsFingerprint);

public sealed record PreparedTicketPublicationZulipReference(
    string AssociationId,
    string TicketKey,
    string Reference,
    string? HydrationId,
    long? HydrationRowId);

public sealed record PreparedTicketPublicationEnrichmentSource(
    string RunId,
    string SnapshotId,
    string SnapshotSha256,
    long AuthoringEpoch,
    long Sequence,
    int SchemaVersion,
    long SizeBytes,
    int ExportedTicketCount);

/// <summary>
/// Private, durable recipe input. Exact receipt/reference coordinates and
/// per-row digests permit restart checks without storing authored bodies.
/// </summary>
public sealed record PreparedTicketPublicationEnrichmentInput(
    int RecipeVersion,
    PreparedTicketPublicationEnrichmentSource Source,
    string SourceCorpusFingerprint,
    IReadOnlyList<PreparedTicketPublicationCorpusItem> Corpus,
    string CorpusFingerprint,
    IReadOnlyList<PreparedTicketPublicationProtectedRowFingerprint> ProtectedRows,
    string ProtectedContentFingerprint,
    IReadOnlyList<PreparedTicketPublicationProtectedGroupingFingerprint> Grouping,
    string RetainedGroupingFingerprint,
    IReadOnlyList<PreparedTicketPublicationZulipReference> ZulipReferences,
    IReadOnlyList<string> AdditionalTicketKeys)
{
    [JsonRequired]
    public string RecipeName { get; init; } = PreparedTicketPublicationEnrichmentContract.RecipeName;
}

/// <summary>
/// Preservation recipe versioning is independent of snapshot schema v3 and
/// publication-proof v1. None of the legacy proof serializers are changed.
/// </summary>
public static class PreparedTicketPublicationEnrichmentContract
{
    public const string RecipeName = "publication-enrichment";
    public const int CurrentVersion = 1;
    public const string StageName = "publication-enrichment-v1";
    public const string LegacyStageName = "publication-metadata";

    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerOptions.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static void EnsureSupportedVersion(int recipeVersion)
    {
        if (recipeVersion != CurrentVersion)
        {
            throw new NotSupportedException(
                $"Unsupported publication-enrichment recipe version {recipeVersion}.");
        }
    }

    public static PreparedTicketPublicationProtectedRowFingerprint FingerprintRow(
        PreparedTicketPublicationProtectedRow row)
        => new(row.Table, row.Scope, row.Key, ComputeSha256(SerializeRow(row)));

    public static byte[] SerializeRow(PreparedTicketPublicationProtectedRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        RequireCoordinate(row.Table);
        RequireCoordinate(row.Scope);
        RequireCoordinate(row.Key);
        ArgumentNullException.ThrowIfNull(row.Values);
        foreach (PreparedTicketPublicationProtectedValue value in row.Values)
        {
            ArgumentNullException.ThrowIfNull(value);
        }
        if (row.Values.Count == 0 ||
            row.Values.Select(value => value.Column).Distinct(StringComparer.Ordinal).Count() !=
                row.Values.Count)
        {
            throw new ArgumentException("Protected rows require unique, explicit columns.");
        }

        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output))
        {
            writer.WriteStartObject();
            writer.WriteString("table", row.Table);
            writer.WriteString("scope", row.Scope);
            writer.WriteString("key", row.Key);
            writer.WriteStartArray("values");
            foreach (PreparedTicketPublicationProtectedValue value in
                     row.Values.OrderBy(value => value.Column, StringComparer.Ordinal))
            {
                RequireCoordinate(value.Column);
                if (value.StorageClass is not ("null" or "integer" or "real" or "text" or "blob") ||
                    (value.StorageClass == "null") != (value.Value is null))
                {
                    throw new ArgumentException("Protected values require an explicit SQLite storage class.");
                }
                writer.WriteStartObject();
                writer.WriteString("column", value.Column);
                writer.WriteString("storageClass", value.StorageClass);
                writer.WriteString("value", value.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    public static string ComputeProtectedContentFingerprint(
        IEnumerable<PreparedTicketPublicationProtectedRow> rows)
        => ComputeProtectedContentFingerprint(rows.Select(FingerprintRow));

    public static string ComputeProtectedContentFingerprint(
        IEnumerable<PreparedTicketPublicationProtectedRowFingerprint> rows)
        => ComputeSha256(SerializeProtectedState(rows));

    public static byte[] SerializeProtectedState(
        IEnumerable<PreparedTicketPublicationProtectedRowFingerprint> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        PreparedTicketPublicationProtectedRowFingerprint[] copied = rows.ToArray();
        foreach (PreparedTicketPublicationProtectedRowFingerprint row in copied)
        {
            ArgumentNullException.ThrowIfNull(row);
        }
        PreparedTicketPublicationProtectedRowFingerprint[] ordered = copied
            .OrderBy(row => row.Table, StringComparer.Ordinal)
            .ThenBy(row => row.Scope, StringComparer.Ordinal)
            .ThenBy(row => row.Key, StringComparer.Ordinal)
            .ToArray();
        HashSet<(string Table, string Scope, string Key)> coordinates = [];
        foreach (PreparedTicketPublicationProtectedRowFingerprint row in ordered)
        {
            RequireCoordinate(row.Table);
            RequireCoordinate(row.Scope);
            RequireCoordinate(row.Key);
            RequireSha256(row.Fingerprint);
            if (!coordinates.Add((row.Table, row.Scope, row.Key)))
            {
                throw new ArgumentException("Protected state contains duplicate row coordinates.");
            }
        }
        return JsonSerializer.SerializeToUtf8Bytes(
            new { recipeVersion = CurrentVersion, rows = ordered },
            SerializerOptions);
    }

    public static string ComputeRetainedGroupingFingerprint(
        IEnumerable<PreparedTicketPublicationProtectedGroupingFingerprint> partitions)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        PreparedTicketPublicationProtectedGroupingFingerprint[] copied = partitions.ToArray();
        foreach (PreparedTicketPublicationProtectedGroupingFingerprint partition in copied)
        {
            ArgumentNullException.ThrowIfNull(partition);
        }
        PreparedTicketPublicationProtectedGroupingFingerprint[] ordered = copied
            .OrderBy(partition => partition.PartitionKey, StringComparer.Ordinal)
            .ToArray();
        foreach (PreparedTicketPublicationProtectedGroupingFingerprint partition in ordered)
        {
            RequireCoordinate(partition.PartitionKey);
            RequireSha256(partition.CorpusFingerprint);
            RequireSha256(partition.OutputFingerprint);
            RequireSha256(partition.ProtectedRowsFingerprint);
        }
        // Reuse the public grouping serialization for its semantic portion;
        // supplement it with exact IDs, raw order columns, and partition corpus.
        string semanticFingerprint = PreparedTicketPublicationContract.ComputeGroupingFingerprint(
            ordered.Select(partition => new PreparedTicketPublicationGroupingPartition(
                partition.PartitionKey, partition.OutputFingerprint)));
        return ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                recipeVersion = CurrentVersion,
                semanticFingerprint,
                partitions = ordered,
            },
            SerializerOptions));
    }

    public static string ComputeInputFingerprint(PreparedTicketPublicationEnrichmentInput input)
        => ComputeSha256(SerializeInput(input));

    public static string SerializeInputJson(PreparedTicketPublicationEnrichmentInput input)
        => Encoding.UTF8.GetString(SerializeInput(input));

    public static byte[] SerializeInput(PreparedTicketPublicationEnrichmentInput input)
    {
        ValidateInput(input);
        PreparedTicketPublicationEnrichmentInput ordered = input with
        {
            Corpus = input.Corpus
                .OrderBy(item => item.TicketKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.TicketKey, StringComparer.Ordinal).ToArray(),
            ProtectedRows = input.ProtectedRows
                .OrderBy(row => row.Table, StringComparer.Ordinal)
                .ThenBy(row => row.Scope, StringComparer.Ordinal)
                .ThenBy(row => row.Key, StringComparer.Ordinal).ToArray(),
            Grouping = input.Grouping
                .OrderBy(partition => partition.PartitionKey, StringComparer.Ordinal).ToArray(),
            ZulipReferences = input.ZulipReferences
                .OrderBy(reference => reference.TicketKey, StringComparer.Ordinal)
                .ThenBy(reference => reference.Reference, StringComparer.Ordinal)
                .ThenBy(reference => reference.AssociationId, StringComparer.Ordinal).ToArray(),
            AdditionalTicketKeys = input.AdditionalTicketKeys
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(key => key, StringComparer.Ordinal).ToArray(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(ordered, SerializerOptions);
    }

    public static PreparedTicketPublicationEnrichmentInput ParseInput(string recipeInputJson)
    {
        using JsonDocument document = JsonDocument.Parse(recipeInputJson);
        EnsureUniqueProperties(document.RootElement);
        PreparedTicketPublicationEnrichmentInput input =
            JsonSerializer.Deserialize<PreparedTicketPublicationEnrichmentInput>(
                recipeInputJson, SerializerOptions)
            ?? throw new JsonException("Publication enrichment input is empty.");
        ValidateInput(input);
        return input;
    }

    private static void ValidateInput(PreparedTicketPublicationEnrichmentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.RecipeName != RecipeName)
        {
            throw new NotSupportedException($"Unsupported publication enrichment recipe '{input.RecipeName}'.");
        }
        EnsureSupportedVersion(input.RecipeVersion);
        ArgumentNullException.ThrowIfNull(input.Source);
        RequireCoordinate(input.Source.RunId);
        RequireCoordinate(input.Source.SnapshotId);
        RequireSha256(input.Source.SnapshotSha256);
        ArgumentOutOfRangeException.ThrowIfNegative(input.Source.AuthoringEpoch);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Source.Sequence, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Source.SizeBytes, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(input.Source.ExportedTicketCount);
        _ = PreparedTicketSnapshotSchemaResolver.Resolve(input.Source.SchemaVersion);
        RequireSha256(input.SourceCorpusFingerprint);
        ArgumentNullException.ThrowIfNull(input.Corpus);
        ArgumentNullException.ThrowIfNull(input.ProtectedRows);
        ArgumentNullException.ThrowIfNull(input.Grouping);
        ArgumentNullException.ThrowIfNull(input.ZulipReferences);
        ArgumentNullException.ThrowIfNull(input.AdditionalTicketKeys);
        if (PreparedTicketPublicationContract.ComputeCorpusFingerprint(input.Corpus) !=
                input.CorpusFingerprint ||
            ComputeProtectedContentFingerprint(input.ProtectedRows) !=
                input.ProtectedContentFingerprint ||
            ComputeRetainedGroupingFingerprint(input.Grouping) !=
                input.RetainedGroupingFingerprint)
        {
            throw new ArgumentException("Publication enrichment input fingerprints do not match its frozen inventory.");
        }

        HashSet<string> tickets = input.Corpus.Select(item => item.TicketKey)
            .ToHashSet(StringComparer.Ordinal);
        if (input.AdditionalTicketKeys.Any(key => !tickets.Contains(key)) ||
            input.AdditionalTicketKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                input.AdditionalTicketKeys.Count ||
            (long)input.Source.ExportedTicketCount + input.AdditionalTicketKeys.Count !=
                input.Corpus.Count)
        {
            throw new ArgumentException("Publication enrichment input has an invalid corpus comparison.");
        }
        HashSet<string> associationIds = new(StringComparer.Ordinal);
        HashSet<string> associationTickets = input.Corpus.Select(item => item.TicketKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<(string TicketKey, string Reference)> associations = [];
        HashSet<string> hydrationIds = new(StringComparer.Ordinal);
        HashSet<long> hydrationRowIds = [];
        foreach (PreparedTicketPublicationZulipReference reference in input.ZulipReferences)
        {
            ArgumentNullException.ThrowIfNull(reference);
            RequireCoordinate(reference.AssociationId);
            RequireCoordinate(reference.Reference);
            if (!associationTickets.Contains(reference.TicketKey) ||
                !associationIds.Add(reference.AssociationId) ||
                !associations.Add((reference.TicketKey.ToUpperInvariant(), reference.Reference)) ||
                (reference.HydrationId is null) != (reference.HydrationRowId is null) ||
                reference.HydrationRowId is <= 0)
            {
                throw new ArgumentException("Publication enrichment input contains invalid accepted Zulip coordinates.");
            }
            if (reference.HydrationId is not null)
            {
                RequireCoordinate(reference.HydrationId);
                if (!hydrationIds.Add(reference.HydrationId) ||
                    !hydrationRowIds.Add(reference.HydrationRowId!.Value))
                {
                    throw new ArgumentException("Publication enrichment input contains ambiguous hydration IDs.");
                }
            }
        }
    }

    private static void EnsureUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException("Publication enrichment input contains duplicate properties.");
                }
                EnsureUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                EnsureUniqueProperties(item);
            }
        }
    }

    private static void RequireCoordinate(string value)
        => ArgumentException.ThrowIfNullOrWhiteSpace(value);

    private static void RequireSha256(string value)
    {
        RequireCoordinate(value);
        if (value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Publication enrichment fingerprints must be canonical lower-case SHA-256 values.");
        }
    }

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
