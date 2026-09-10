using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Publishing.Tickets;

public sealed record TicketSiteManifestFilters(
    string? Spec,
    string? Project,
    string? Wg);

public sealed record TicketSiteManifest(
    string SiteKind,
    string ProcessorKind,
    string RunId,
    string SnapshotId,
    long AuthoringEpoch,
    long SnapshotSequence,
    int SnapshotSchemaVersion,
    string SnapshotSha256,
    long SnapshotSizeBytes,
    string EmbeddedDbSha256,
    long EmbeddedDbSizeBytes,
    int IncludedItemCount,
    int IncludedReceiptCount,
    IReadOnlyDictionary<string, long> TableCounts,
    TicketSiteManifestFilters Filters,
    string Title,
    string RendererAssetsVersion,
    string BuildIdentity,
    string OutputPath,
    DateTimeOffset GeneratedAt)
{
    public const string FileName = "site-manifest.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static TicketSiteManifest Create(
        string siteKind,
        AuthoringSnapshotDescriptor descriptor,
        int includedItemCount,
        IReadOnlyDictionary<string, long> tableCounts,
        ResolvedFilters filters,
        string title,
        string rendererAssetsVersion,
        string embeddedDbSha256,
        long embeddedDbSizeBytes,
        string outputPath,
        DateTimeOffset generatedAt)
    {
        string buildIdentity = ComputeBuildIdentity(
            siteKind,
            filters,
            title,
            rendererAssetsVersion,
            descriptor.Sha256,
            embeddedDbSha256);
        return new(
            siteKind,
            descriptor.ProcessorKind,
            descriptor.RunId,
            descriptor.SnapshotId,
            descriptor.AuthoringEpoch,
            descriptor.Sequence,
            descriptor.SchemaVersion,
            descriptor.Sha256,
            descriptor.SizeBytes,
            embeddedDbSha256,
            embeddedDbSizeBytes,
            includedItemCount,
            descriptor.ReceiptCount,
            tableCounts,
            new TicketSiteManifestFilters(
                filters.Specification,
                filters.Project,
                filters.WorkGroup),
            title,
            rendererAssetsVersion,
            buildIdentity,
            outputPath,
            generatedAt);
    }

    internal static string ComputeBuildIdentity(
        string siteKind,
        ResolvedFilters filters,
        string title,
        string rendererAssetsVersion,
        string sourceIdentity,
        string embeddedDbSha256)
    {
        string input = string.Join(
            "\n",
            "site-kind=" + siteKind,
            "spec=" + (filters.Specification ?? string.Empty),
            "project=" + (filters.Project ?? string.Empty),
            "wg=" + (filters.WorkGroup ?? string.Empty),
            "title=" + title,
            "renderer-assets=" + rendererAssetsVersion,
            "source=" + sourceIdentity,
            "embedded-db=" + embeddedDbSha256);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
            .ToLowerInvariant();
    }

    internal static async Task WriteAsync(
        string directory,
        TicketSiteManifest manifest,
        CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, FileName),
            JsonSerializer.Serialize(manifest, SerializerOptions),
            ct).ConfigureAwait(false);
    }

    public static string ToSummaryJson(TicketSiteManifest manifest)
        => JsonSerializer.Serialize(manifest, SerializerOptions);

    public static TicketSiteManifest Read(string path)
        => JsonSerializer.Deserialize<TicketSiteManifest>(
            File.ReadAllText(path),
            SerializerOptions)
        ?? throw new InvalidOperationException($"Site manifest '{path}' is empty.");

    public static async Task<TicketSiteManifest> ReadAsync(
        string path,
        CancellationToken ct = default)
        => JsonSerializer.Deserialize<TicketSiteManifest>(
            await File.ReadAllTextAsync(path, ct).ConfigureAwait(false),
            SerializerOptions)
        ?? throw new InvalidOperationException($"Site manifest '{path}' is empty.");
}
