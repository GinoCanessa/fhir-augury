using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FhirAugury.Common.IO;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Publishing.Tickets;

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

    public static async Task<MetaFilterSet?> TryReadExistingMarkerAsync(
        string subSiteOut,
        CancellationToken ct)
    {
        string markerPath = Path.Combine(subSiteOut, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            return null;
        }
        try
        {
            string json = await File.ReadAllTextAsync(markerPath, ct)
                .ConfigureAwait(false);
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

    public static async Task WriteMarkerAsync(
        string subSiteOut,
        string kind,
        ResolvedFilters filters,
        DateTimeOffset createdAt,
        AuthoringSnapshotDescriptor descriptor,
        CancellationToken ct)
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
            RunId = descriptor.RunId,
            SnapshotId = descriptor.SnapshotId,
            SnapshotSequence = descriptor.Sequence,
        };
        string json = JsonSerializer.Serialize(payload, WriteOptions);
        Directory.CreateDirectory(subSiteOut);
        await File.WriteAllTextAsync(
            Path.Combine(subSiteOut, MarkerFileName),
            json,
            ct).ConfigureAwait(false);
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

    public static async Task ValidateSnapshotStageAsync(
        string stagingDirectory,
        string kind,
        TicketSiteManifest expected,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        List<string> requiredFiles =
        [
            "index.html",
            MarkerFileName,
            TicketSiteManifest.FileName,
            StagedDirectoryPublisher.VersionFileName,
            Path.Combine("assets", "app.js"),
            Path.Combine("assets", "app.css"),
            Path.Combine("assets", "sql-wasm.js"),
            Path.Combine("assets", "sql-wasm.wasm"),
            Path.Combine("assets", "marked.min.js"),
            Path.Combine("assets", "purify.min.js"),
        ];
        if (string.Equals(
                kind,
                PreparerSubSiteEmitter.Kind,
                StringComparison.Ordinal))
        {
            requiredFiles.Add(Path.Combine("assets", "components.js"));
        }
        foreach (string relative in requiredFiles)
        {
            if (!File.Exists(Path.Combine(stagingDirectory, relative)))
            {
                throw new InvalidOperationException(
                    $"Staged {kind} site is missing required file '{relative}'.");
            }
        }

        MetaFilterSet marker =
            await TryReadExistingMarkerAsync(stagingDirectory, ct)
                .ConfigureAwait(false)
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

        TicketSiteManifest actual = await TicketSiteManifest.ReadAsync(
            Path.Combine(stagingDirectory, TicketSiteManifest.FileName),
            ct).ConfigureAwait(false);
        if (!ManifestMatches(actual, expected))
        {
            throw new InvalidOperationException(
                $"Staged {kind} site manifest does not match the validated build summary.");
        }

        await ValidateEmbeddedDatabaseAsync(
            stagingDirectory,
            kind,
            expected,
            ct).ConfigureAwait(false);
    }

    private static bool ManifestMatches(
        TicketSiteManifest actual,
        TicketSiteManifest expected)
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
        string.Equals(actual.DisplayTitle, expected.DisplayTitle, StringComparison.Ordinal) &&
        actual.JiraSourceLastSuccessfulRefreshAt ==
            expected.JiraSourceLastSuccessfulRefreshAt &&
        actual.RendererSchemaVersion == expected.RendererSchemaVersion &&
        ReadinessMatches(
            actual.DiscussionReadiness,
            expected.DiscussionReadiness) &&
        CorpusMatches(actual.DiscussionCorpus, expected.DiscussionCorpus) &&
        actual.TableCounts.Count == expected.TableCounts.Count &&
        actual.TableCounts.All(pair =>
            expected.TableCounts.TryGetValue(pair.Key, out long value) &&
            pair.Value == value);

    private static async Task ValidateEmbeddedDatabaseAsync(
        string stagingDirectory,
        string kind,
        TicketSiteManifest expected,
        CancellationToken ct)
    {
        string html = await File.ReadAllTextAsync(
            Path.Combine(stagingDirectory, "index.html"),
            ct).ConfigureAwait(false);
        byte[] databaseBytes = await ExtractEmbeddedDatabaseAsync(
            html,
            kind,
            ct).ConfigureAwait(false);
        string actualSha256 = ComputeSha256(databaseBytes, ct);
        if (databaseBytes.LongLength != expected.EmbeddedDbSizeBytes ||
            !string.Equals(
                actualSha256,
                expected.EmbeddedDbSha256,
                StringComparison.Ordinal))
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
            if (string.Equals(
                    kind,
                    PreparerSubSiteEmitter.Kind,
                    StringComparison.Ordinal))
            {
                await connection.CloseAsync().ConfigureAwait(false);
                await ValidateDiscussionDatabaseAsync(
                    validationPath,
                    html,
                    expected,
                    ct).ConfigureAwait(false);
            }
            else
            {
                await ValidateTableCountsAsync(
                    connection,
                    kind,
                    expected.TableCounts,
                    ct).ConfigureAwait(false);
                long actualItems = await ReadCountAsync(
                    connection,
                    "planned_tickets",
                    ct).ConfigureAwait(false);
                if (actualItems != expected.IncludedItemCount)
                {
                    throw new InvalidOperationException(
                        $"Staged {kind} embedded database item count mismatch: " +
                        $"manifest={expected.IncludedItemCount}, " +
                        $"database={actualItems}.");
                }
                long actualReceipts = await ReadCountAsync(
                    connection,
                    "authoring_result_receipts",
                    ct).ConfigureAwait(false);
                if (actualReceipts != expected.IncludedReceiptCount)
                {
                    throw new InvalidOperationException(
                        $"Staged {kind} embedded database receipt count mismatch: " +
                        $"manifest={expected.IncludedReceiptCount}, " +
                        $"database={actualReceipts}.");
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

    private static async Task ValidateDiscussionDatabaseAsync(
        string databasePath,
        string html,
        TicketSiteManifest expected,
        CancellationToken ct)
    {
        DiscussionSiteDatabaseValidator.ValidationResult validation =
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                databasePath,
                ct).ConfigureAwait(false);
        EnsureTableCountsMatch(
            PreparerSubSiteEmitter.Kind,
            validation.TableCounts,
            expected.TableCounts);
        if (!validation.TableCounts.TryGetValue("tickets", out long ticketCount) ||
            ticketCount != expected.IncludedItemCount)
        {
            throw new InvalidOperationException(
                "Staged preparer embedded database item count mismatch: " +
                $"manifest={expected.IncludedItemCount}, database={ticketCount}.");
        }

        if (expected.RendererSchemaVersion is not int rendererSchemaVersion ||
            string.IsNullOrWhiteSpace(expected.DisplayTitle) ||
            expected.DiscussionReadiness is null ||
            expected.DiscussionCorpus is null)
        {
            throw new InvalidOperationException(
                "Staged preparer manifest is missing discussion presentation metadata.");
        }
        TicketSitePresentation manifestPresentation = new(
            rendererSchemaVersion,
            expected.Title,
            expected.DisplayTitle,
            expected.JiraSourceLastSuccessfulRefreshAt,
            new ResolvedFilters(
                expected.Filters.Spec,
                expected.Filters.Project,
                expected.Filters.Wg),
            expected.DiscussionReadiness,
            expected.DiscussionCorpus);
        TicketSitePresentation injectedPresentation =
            ReadInjectedPresentation(html);
        string expectedPresentationJson =
            TicketSitePresentationJson.Serialize(manifestPresentation);
        if (!string.Equals(
                TicketSitePresentationJson.Serialize(validation.Presentation),
                expectedPresentationJson,
                StringComparison.Ordinal) ||
            !string.Equals(
                TicketSitePresentationJson.Serialize(injectedPresentation),
                expectedPresentationJson,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Staged preparer presentation does not match its renderer database and manifest.");
        }
        string encodedTitle = WebUtility.HtmlEncode(manifestPresentation.SiteName);
        foreach (string tag in new[] { "title", "h1" })
        {
            string opener = $"<{tag}>";
            string element = $"{opener}{encodedTitle}</{tag}>";
            int start = html.IndexOf(opener, StringComparison.Ordinal);
            if (start < 0 ||
                html.IndexOf(opener, start + opener.Length, StringComparison.Ordinal) >= 0 ||
                !html.AsSpan(start).StartsWith(element, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Staged preparer initial {tag} does not match its discussion presentation.");
            }
        }
    }

    private static async Task ValidateTableCountsAsync(
        SqliteConnection connection,
        string kind,
        IReadOnlyDictionary<string, long> expectedCounts,
        CancellationToken ct)
    {
        Dictionary<string, long> actualCounts =
            new(StringComparer.Ordinal);
        foreach (string table in expectedCounts.Keys)
        {
            actualCounts[table] =
                await ReadCountAsync(connection, table, ct)
                    .ConfigureAwait(false);
        }
        EnsureTableCountsMatch(kind, actualCounts, expectedCounts);
    }

    private static bool ReadinessMatches(
        DiscussionPublicationReadiness? actual,
        DiscussionPublicationReadiness? expected)
        => actual is null || expected is null
            ? actual is null && expected is null
            : string.Equals(
                TicketSitePresentationJson.Serialize(actual),
                TicketSitePresentationJson.Serialize(expected),
                StringComparison.Ordinal);

    private static bool CorpusMatches(
        DiscussionCorpusSummary? actual,
        DiscussionCorpusSummary? expected)
        => actual is null || expected is null
            ? actual is null && expected is null
            : string.Equals(
                TicketSitePresentationJson.Serialize(actual),
                TicketSitePresentationJson.Serialize(expected),
                StringComparison.Ordinal);

    private static void EnsureTableCountsMatch(
        string kind,
        IReadOnlyDictionary<string, long> actualCounts,
        IReadOnlyDictionary<string, long> expectedCounts)
    {
        foreach ((string table, long expected) in expectedCounts)
        {
            if (!actualCounts.TryGetValue(table, out long actual) ||
                actual != expected)
            {
                throw new InvalidOperationException(
                    $"Staged {kind} embedded database count mismatch for " +
                    $"'{table}': manifest={expected}, database=" +
                    (actualCounts.ContainsKey(table)
                        ? actual.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                        : "missing") +
                    ".");
            }
        }
        if (actualCounts.Count != expectedCounts.Count)
        {
            throw new InvalidOperationException(
                $"Staged {kind} embedded database table-count inventory differs " +
                "from its manifest.");
        }
    }

    private static TicketSitePresentation ReadInjectedPresentation(string html)
    {
        const string startMarker =
            "<script id=\"site-presentation\" type=\"application/json\">";
        const string endMarker = "</script>";
        int start = html.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException(
                "Staged preparer site does not contain presentation JSON.");
        }
        start += startMarker.Length;
        int end = html.IndexOf(endMarker, start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException(
                "Staged preparer site has invalid presentation JSON.");
        }
        try
        {
            string json = html[start..end];
            TicketSitePresentation presentation =
                TicketSitePresentationJson.Deserialize(json);
            if (!string.Equals(
                json,
                TicketSitePresentationJson.Serialize(presentation),
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Staged preparer presentation JSON is not canonical.");
            }
            return presentation;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Staged preparer site has invalid presentation JSON.",
                ex);
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

    private static async Task<byte[]> ExtractEmbeddedDatabaseAsync(
        string html,
        string kind,
        CancellationToken ct)
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
            ct.ThrowIfCancellationRequested();
            compressed = Convert.FromBase64String(html[start..end]);
            ct.ThrowIfCancellationRequested();
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Staged {kind} site has an invalid embedded database encoding.",
                ex);
        }

        using MemoryStream input = new(compressed);
        await using GZipStream gzip = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        await gzip.CopyToAsync(output, ct).ConfigureAwait(false);
        return output.ToArray();
    }

    private static string ComputeSha256(
        byte[] bytes,
        CancellationToken ct)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int chunkSize = 128 * 1024;
        for (int offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(chunkSize, bytes.Length - offset);
            hash.AppendData(bytes, offset, count);
        }
        ct.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }
}
