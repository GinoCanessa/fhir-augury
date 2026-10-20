using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Tools.NotesSite;

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
    int IncludedNoteCount,
    int IncludedReceiptCount,
    IReadOnlyDictionary<string, long> TableCounts,
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
    };

    public static SiteBuildManifest Create(
        AuthoringSnapshotDescriptor descriptor,
        IReadOnlyDictionary<string, long> tableCounts,
        string title,
        string rendererAssetsVersion,
        string embeddedDbSha256,
        long embeddedDbSizeBytes,
        string outputPath,
        DateTimeOffset generatedAt)
    {
        const string siteKind = "notes";
        string buildIdentity = ComputeBuildIdentity(
            siteKind,
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
            checked((int)tableCounts["notes"]),
            descriptor.ReceiptCount,
            tableCounts,
            title,
            rendererAssetsVersion,
            buildIdentity,
            outputPath,
            generatedAt);
    }

    public static string ComputeBuildIdentity(
        string siteKind,
        string title,
        string rendererAssetsVersion,
        string sourceIdentity,
        string embeddedDbSha256)
    {
        string input = string.Join(
            "\n",
            "site-kind=" + siteKind,
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

    public static SiteBuildManifest Read(string path)
        => JsonSerializer.Deserialize<SiteBuildManifest>(
            File.ReadAllText(path),
            SerializerOptions)
        ?? throw new InvalidOperationException($"Site manifest '{path}' is empty.");

    public static string ToSummaryJson(SiteBuildManifest manifest)
        => JsonSerializer.Serialize(manifest, SerializerOptions);
}
