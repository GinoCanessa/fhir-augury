using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Tools.TicketSite;

internal sealed record SiteBuildManifest(
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
    MetaFilters Filters,
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

    public static SiteBuildManifest Create(
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
            new MetaFilters
            {
                Spec = filters.Specification,
                Project = filters.Project,
                Wg = filters.WorkGroup,
            },
            title,
            rendererAssetsVersion,
            buildIdentity,
            outputPath,
            generatedAt);
    }

    public static string ComputeBuildIdentity(
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

    public static void Write(string directory, SiteBuildManifest manifest)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, FileName),
            JsonSerializer.Serialize(manifest, SerializerOptions));
    }

    public static string ToSummaryJson(SiteBuildManifest manifest)
        => JsonSerializer.Serialize(manifest, SerializerOptions);

    public static SiteBuildManifest Read(string path)
        => JsonSerializer.Deserialize<SiteBuildManifest>(
            File.ReadAllText(path),
            SerializerOptions)
        ?? throw new InvalidOperationException($"Site manifest '{path}' is empty.");
}
