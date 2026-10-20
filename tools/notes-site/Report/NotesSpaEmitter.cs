using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.Json;
using FhirAugury.Common.IO;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Tools.NotesSite.Report;

/// <summary>
/// Emits a single self-contained report: an <c>index.html</c> plus an
/// <c>assets/</c> folder. The notes SQLite DB is inlined as base64 into
/// <c>window.__DB__</c> and loaded in-browser via sql.js (no network), modelled
/// on fhir-spec-review's <c>ReviewSpaEmitter</c>. Read-only consumer of the
/// notes DB.
/// </summary>
internal sealed class NotesSpaEmitter
{
    private const string ReportPrefix = "web-assets/report/";
    private const string TemplateName = "web-assets/report/index.template.html";
    private const string TitleMarker = "<!-- __TITLE__ -->";
    private const string DbBlobMarker = "<!-- __DB_BLOB__ -->";
    private const string ProvenanceMarker = "<!-- __PROVENANCE__ -->";

    private readonly byte[] _snapshotBytes;
    private readonly AuthoringSnapshotDescriptor _snapshotDescriptor;
    private readonly string _title;

    public static string RendererAssetsVersion =>
        StagedDirectoryPublisher.GetRendererAssetsVersion(
            typeof(NotesSpaEmitter).Assembly);

    public NotesSpaEmitter(
        byte[] snapshotBytes,
        AuthoringSnapshotDescriptor descriptor,
        string title)
    {
        ArgumentNullException.ThrowIfNull(snapshotBytes);
        ArgumentNullException.ThrowIfNull(descriptor);
        _snapshotBytes = snapshotBytes;
        _snapshotDescriptor = descriptor;
        _title = title;
    }

    public void Emit(string outDir)
    {
        // Force/overwrite cleanup: delete the output directory so stale assets
        // from a prior run never linger next to the SPA.
        if (Directory.Exists(outDir))
        {
            Directory.Delete(outDir, recursive: true);
        }
        Directory.CreateDirectory(outDir);
        string assetsDir = Path.Combine(outDir, "assets");
        Directory.CreateDirectory(assetsDir);

        string base64 = Convert.ToBase64String(GzipBytes(_snapshotBytes));
        string blobScript = $"<script>window.__DB__='{base64}';window.__DBGZ__=1;</script>";
        string provenanceScript = BuildSnapshotProvenanceScript(_snapshotDescriptor);
        string encodedTitle = WebUtility.HtmlEncode(_title);

        Assembly asm = typeof(NotesSpaEmitter).Assembly;
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(ReportPrefix, StringComparison.Ordinal)) continue;

            using Stream stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing embedded resource: {name}");

            if (string.Equals(name, TemplateName, StringComparison.Ordinal))
            {
                using StreamReader reader = new(stream);
                string template = reader.ReadToEnd();
                string html = template
                    .Replace(TitleMarker, encodedTitle, StringComparison.Ordinal)
                    .Replace(ProvenanceMarker, provenanceScript, StringComparison.Ordinal)
                    .Replace(DbBlobMarker, blobScript, StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(outDir, "index.html"), html);
            }
            else
            {
                string relative = name.Substring(ReportPrefix.Length);
                string outFile = Path.Combine(assetsDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
                using FileStream fs = File.Create(outFile);
                stream.CopyTo(fs);
            }
        }
    }

    private static byte[] GzipBytes(byte[] raw)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(raw, 0, raw.Length);
        }
        return output.ToArray();
    }

    private static string BuildSnapshotProvenanceScript(
        AuthoringSnapshotDescriptor descriptor)
    {
        string json = JsonSerializer.Serialize(new
        {
            processorKind = descriptor.ProcessorKind,
            runId = descriptor.RunId,
            snapshotId = descriptor.SnapshotId,
            authoringEpoch = descriptor.AuthoringEpoch,
            snapshotSequence = descriptor.Sequence,
            snapshotCreatedAt = descriptor.CreatedAt,
            noteCount = descriptor.TableCounts.TryGetValue("notes", out long count)
                ? count
                : descriptor.ItemCount,
        });
        return $"<script>window.__RUN__={json};</script>";
    }
}
