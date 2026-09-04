using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FhirAugury.Common.IO;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketSite;

internal sealed class MetaFilterSet
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("filters")]
    public MetaFilters Filters { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("runId")]
    public string? RunId { get; set; }

    [JsonPropertyName("snapshotId")]
    public string? SnapshotId { get; set; }

    [JsonPropertyName("snapshotSequence")]
    public long? SnapshotSequence { get; set; }
}

internal sealed class MetaFilters
{
    [JsonPropertyName("spec")]
    public string? Spec { get; set; }

    [JsonPropertyName("project")]
    public string? Project { get; set; }

    [JsonPropertyName("wg")]
    public string? Wg { get; set; }
}

internal static class OutputDirGuard
{
    public const string MarkerFileName = ".ticket-site.meta";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MetaFilterSet? TryReadExistingMarker(string subSiteOut)
    {
        string markerPath = Path.Combine(subSiteOut, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            return null;
        }
        try
        {
            string json = File.ReadAllText(markerPath);
            return JsonSerializer.Deserialize<MetaFilterSet>(json);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void WriteMarker(
        string subSiteOut,
        string kind,
        ResolvedFilters filters,
        DateTimeOffset createdAt,
        AuthoringSnapshotDescriptor? descriptor = null)
    {
        MetaFilterSet payload = new()
        {
            Kind = kind,
            Filters = new MetaFilters
            {
                Spec = filters.Specification,
                Project = filters.Project,
                Wg = filters.WorkGroup,
            },
            CreatedAt = createdAt.ToString("O"),
            RunId = descriptor?.RunId,
            SnapshotId = descriptor?.SnapshotId,
            SnapshotSequence = descriptor?.Sequence,
        };
        string json = JsonSerializer.Serialize(payload, WriteOptions);
        Directory.CreateDirectory(subSiteOut);
        File.WriteAllText(Path.Combine(subSiteOut, MarkerFileName), json);
    }

    public static bool FilterSetsMatch(MetaFilterSet? existing, ResolvedFilters incoming)
    {
        if (existing is null)
        {
            return false;
        }
        MetaFilters existingFilters = existing.Filters ?? new MetaFilters();
        return string.Equals(existingFilters.Spec, incoming.Specification, StringComparison.Ordinal)
            && string.Equals(existingFilters.Project, incoming.Project, StringComparison.Ordinal)
            && string.Equals(existingFilters.Wg, incoming.WorkGroup, StringComparison.Ordinal);
    }

    public static bool KindMatches(MetaFilterSet? existing, string incomingKind)
    {
        if (existing is null) return true; // first build into this folder
        return string.IsNullOrEmpty(existing.Kind) || string.Equals(existing.Kind, incomingKind, StringComparison.Ordinal);
    }

    public static Task ValidateLegacyReplacementAsync(
        string outputDirectory,
        string kind,
        ResolvedFilters filters,
        bool force,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        MetaFilterSet? existing = TryReadExistingMarker(outputDirectory);
        if (existing is null)
        {
            if (!force)
            {
                throw new InvalidOperationException(
                    $"Output sub-site directory '{outputDirectory}' has no valid ownership marker. " +
                    "Pass --force to replace it.");
            }
            return Task.CompletedTask;
        }
        if (!KindMatches(existing, kind))
        {
            throw new InvalidOperationException(
                $"Output sub-site directory '{outputDirectory}' was produced as kind " +
                $"'{existing.Kind}' but the current build is '{kind}'.");
        }
        if (!force && !FilterSetsMatch(existing, filters))
        {
            throw new InvalidOperationException(
                $"Output sub-site directory '{outputDirectory}' was produced with a " +
                "different filter set. Pass --force to overwrite, or choose a different --out.");
        }
        return Task.CompletedTask;
    }

    public static Task ValidateSnapshotStageAsync(
        string stagingDirectory,
        string kind,
        SiteBuildManifest expected,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string[] requiredFiles =
        [
            "index.html",
            MarkerFileName,
            SiteBuildManifest.FileName,
            StagedDirectoryPublisher.VersionFileName,
            Path.Combine("assets", "app.js"),
            Path.Combine("assets", "app.css"),
            Path.Combine("assets", "sql-wasm.js"),
            Path.Combine("assets", "sql-wasm.wasm"),
            Path.Combine("assets", "marked.min.js"),
            Path.Combine("assets", "purify.min.js"),
        ];
        foreach (string relative in requiredFiles)
        {
            if (!File.Exists(Path.Combine(stagingDirectory, relative)))
            {
                throw new InvalidOperationException(
                    $"Staged {kind} site is missing required file '{relative}'.");
            }
        }

        MetaFilterSet marker = TryReadExistingMarker(stagingDirectory)
            ?? throw new InvalidOperationException(
                $"Staged {kind} site has an invalid ownership marker.");
        if (!string.Equals(marker.Kind, kind, StringComparison.Ordinal) ||
            !string.Equals(marker.RunId, expected.RunId, StringComparison.Ordinal) ||
            !string.Equals(marker.SnapshotId, expected.SnapshotId, StringComparison.Ordinal) ||
            marker.SnapshotSequence != expected.SnapshotSequence)
        {
            throw new InvalidOperationException(
                $"Staged {kind} site ownership marker does not match its snapshot.");
        }

        SiteBuildManifest actual = SiteBuildManifest.Read(
            Path.Combine(stagingDirectory, SiteBuildManifest.FileName));
        if (!ManifestMatches(actual, expected))
        {
            throw new InvalidOperationException(
                $"Staged {kind} site manifest does not match the validated build summary.");
        }

        return ValidateEmbeddedDatabaseAsync(
            stagingDirectory,
            kind,
            expected.EmbeddedDbSha256,
            expected.EmbeddedDbSizeBytes,
            expected.TableCounts,
            expected.SiteKind == PreparerSubSiteEmitter.Kind
                ? "prepared_tickets"
                : "planned_tickets",
            expected.IncludedItemCount,
            expected.IncludedReceiptCount,
            ct);
    }

    public static async Task ValidateLegacyStageAsync(
        string stagingDirectory,
        string kind,
        ResolvedFilters filters,
        string embeddedDbSha256,
        long embeddedDbSizeBytes,
        IReadOnlyDictionary<string, long> tableCounts,
        CancellationToken ct)
    {
        string[] requiredFiles =
        [
            "index.html",
            MarkerFileName,
            StagedDirectoryPublisher.VersionFileName,
            Path.Combine("assets", "app.js"),
            Path.Combine("assets", "app.css"),
            Path.Combine("assets", "sql-wasm.js"),
            Path.Combine("assets", "sql-wasm.wasm"),
            Path.Combine("assets", "marked.min.js"),
            Path.Combine("assets", "purify.min.js"),
        ];
        foreach (string relative in requiredFiles)
        {
            if (!File.Exists(Path.Combine(stagingDirectory, relative)))
            {
                throw new InvalidOperationException(
                    $"Staged {kind} site is missing required file '{relative}'.");
            }
        }

        MetaFilterSet marker = TryReadExistingMarker(stagingDirectory)
            ?? throw new InvalidOperationException(
                $"Staged {kind} site has an invalid ownership marker.");
        if (!KindMatches(marker, kind) || !FilterSetsMatch(marker, filters))
        {
            throw new InvalidOperationException(
                $"Staged {kind} site ownership marker does not match its build.");
        }

        await ValidateEmbeddedDatabaseAsync(
            stagingDirectory,
            kind,
            embeddedDbSha256,
            embeddedDbSizeBytes,
            tableCounts,
            itemTable: null,
            expectedItemCount: null,
            expectedReceiptCount: null,
            ct: ct).ConfigureAwait(false);
    }

    private static bool ManifestMatches(
        SiteBuildManifest actual,
        SiteBuildManifest expected)
        =>
        string.Equals(actual.SiteKind, expected.SiteKind, StringComparison.Ordinal) &&
        string.Equals(actual.ProcessorKind, expected.ProcessorKind, StringComparison.Ordinal) &&
        string.Equals(actual.RunId, expected.RunId, StringComparison.Ordinal) &&
        string.Equals(actual.SnapshotId, expected.SnapshotId, StringComparison.Ordinal) &&
        actual.AuthoringEpoch == expected.AuthoringEpoch &&
        actual.SnapshotSequence == expected.SnapshotSequence &&
        actual.SnapshotSchemaVersion == expected.SnapshotSchemaVersion &&
        string.Equals(actual.SnapshotSha256, expected.SnapshotSha256, StringComparison.Ordinal) &&
        actual.SnapshotSizeBytes == expected.SnapshotSizeBytes &&
        string.Equals(actual.EmbeddedDbSha256, expected.EmbeddedDbSha256, StringComparison.Ordinal) &&
        actual.EmbeddedDbSizeBytes == expected.EmbeddedDbSizeBytes &&
        actual.IncludedItemCount == expected.IncludedItemCount &&
        actual.IncludedReceiptCount == expected.IncludedReceiptCount &&
        string.Equals(actual.Filters.Spec, expected.Filters.Spec, StringComparison.Ordinal) &&
        string.Equals(actual.Filters.Project, expected.Filters.Project, StringComparison.Ordinal) &&
        string.Equals(actual.Filters.Wg, expected.Filters.Wg, StringComparison.Ordinal) &&
        string.Equals(actual.Title, expected.Title, StringComparison.Ordinal) &&
        string.Equals(actual.RendererAssetsVersion, expected.RendererAssetsVersion, StringComparison.Ordinal) &&
        string.Equals(actual.BuildIdentity, expected.BuildIdentity, StringComparison.Ordinal) &&
        string.Equals(actual.OutputPath, expected.OutputPath, StringComparison.Ordinal) &&
        actual.GeneratedAt == expected.GeneratedAt &&
        actual.TableCounts.Count == expected.TableCounts.Count &&
        actual.TableCounts.All(pair =>
            expected.TableCounts.TryGetValue(pair.Key, out long value) &&
            pair.Value == value);

    private static async Task ValidateEmbeddedDatabaseAsync(
        string stagingDirectory,
        string kind,
        string expectedSha256,
        long expectedSizeBytes,
        IReadOnlyDictionary<string, long> expectedCounts,
        string? itemTable,
        int? expectedItemCount,
        int? expectedReceiptCount,
        CancellationToken ct)
    {
        string html = await File.ReadAllTextAsync(
            Path.Combine(stagingDirectory, "index.html"),
            ct).ConfigureAwait(false);
        byte[] databaseBytes = ExtractEmbeddedDatabase(html, kind);
        string actualSha256 =
            Convert.ToHexString(SHA256.HashData(databaseBytes)).ToLowerInvariant();
        if (databaseBytes.LongLength != expectedSizeBytes ||
            !string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Staged {kind} embedded database checksum or size does not match its manifest.");
        }

        string validationPath = Path.Combine(
            stagingDirectory,
            $".embedded-validation-{Guid.NewGuid():N}.db");
        try
        {
            await File.WriteAllBytesAsync(validationPath, databaseBytes, ct)
                .ConfigureAwait(false);
            await using SqliteConnection connection = new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = validationPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
            await connection.OpenAsync(ct).ConfigureAwait(false);
            foreach ((string table, long expected) in expectedCounts)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
                long actual = Convert.ToInt64(
                    await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (actual != expected)
                {
                    throw new InvalidOperationException(
                        $"Staged {kind} embedded database count mismatch for '{table}': " +
                        $"manifest={expected}, database={actual}.");
                }
            }
            if (itemTable is not null && expectedItemCount is not null)
            {
                long actualItems = await ReadCountAsync(connection, itemTable, ct)
                    .ConfigureAwait(false);
                if (actualItems != expectedItemCount)
                {
                    throw new InvalidOperationException(
                        $"Staged {kind} embedded database item count mismatch: " +
                        $"manifest={expectedItemCount}, database={actualItems}.");
                }
            }
            if (expectedReceiptCount is not null)
            {
                long actualReceipts = await ReadCountAsync(
                    connection,
                    "authoring_result_receipts",
                    ct).ConfigureAwait(false);
                if (actualReceipts != expectedReceiptCount)
                {
                    throw new InvalidOperationException(
                        $"Staged {kind} embedded database receipt count mismatch: " +
                        $"manifest={expectedReceiptCount}, database={actualReceipts}.");
                }
            }
        }
        finally
        {
            try
            {
                File.Delete(validationPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<long> ReadCountAsync(
        SqliteConnection connection,
        string table,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] ExtractEmbeddedDatabase(string html, string kind)
    {
        const string marker = "window.__DB__='";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException(
                $"Staged {kind} site does not contain an embedded database.");
        }
        start += marker.Length;
        int end = html.IndexOf('\'', start);
        if (end < 0)
        {
            throw new InvalidOperationException(
                $"Staged {kind} site has an invalid embedded database.");
        }

        byte[] compressed;
        try
        {
            compressed = Convert.FromBase64String(html[start..end]);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Staged {kind} site has an invalid embedded database encoding.",
                ex);
        }

        using MemoryStream input = new(compressed);
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
