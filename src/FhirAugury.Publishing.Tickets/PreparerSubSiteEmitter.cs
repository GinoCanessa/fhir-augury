using System.IO.Compression;
using System.Net;
using System.Reflection;
using FhirAugury.Common.IO;

namespace FhirAugury.Publishing.Tickets;

/// <summary>
/// Emits the discussion (preparer) sub-site under
/// <c>&lt;rootOut&gt;/discussion/</c> from a validated renderer database and
/// its presentation metadata.
/// </summary>
internal static class PreparerSubSiteEmitter
{
    public const string SubSiteFolder = "discussion";
    public const string Kind = "preparer";

    private const string DiscussionPrefix = "web-assets/discussion/";
    private const string SharedPrefix = "web-assets/shared/";
    private const string TemplateName = "web-assets/discussion/index.template.html";
    private const string TitleMarker = "<!-- __TITLE__ -->";
    private const string DbBlobMarker = "<!-- __DB_BLOB__ -->";
    private const string PresentationMarker = "<!-- __PRESENTATION__ -->";
    private const string AssetVersionMarker = "__ASSET_VERSION__";

    public static string RendererAssetsVersion =>
        StagedDirectoryPublisher.GetRendererAssetsVersion(
            typeof(PreparerSubSiteEmitter).Assembly);

    public static async Task EmitAsync(
        string subSiteOut,
        TicketSitePresentation presentation,
        byte[] dbBytes,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(subSiteOut))
        {
            Directory.Delete(subSiteOut, recursive: true);
        }
        Directory.CreateDirectory(subSiteOut);
        string assetsDir = Path.Combine(subSiteOut, "assets");
        Directory.CreateDirectory(assetsDir);

        Assembly asm = typeof(PreparerSubSiteEmitter).Assembly;
        string[] resourceNames = asm.GetManifestResourceNames();

        string encodedTitle = WebUtility.HtmlEncode(presentation.SiteName);
        byte[] compressed =
            await GzipBytesAsync(dbBytes, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        string base64 = Convert.ToBase64String(compressed);
        ct.ThrowIfCancellationRequested();
        string blobScript = $"<script>window.__DB__='{base64}';window.__DBGZ__=1;</script>";
        string presentationScript =
            "<script id=\"site-presentation\" type=\"application/json\">" +
            TicketSitePresentationJson.Serialize(presentation) +
            "</script>";

        foreach (string name in resourceNames)
        {
            ct.ThrowIfCancellationRequested();
            bool isDiscussion = name.StartsWith(DiscussionPrefix, StringComparison.Ordinal);
            bool isShared = name.StartsWith(SharedPrefix, StringComparison.Ordinal);
            if (!isDiscussion && !isShared) continue;

            using Stream stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing embedded resource: {name}");

            if (string.Equals(name, TemplateName, StringComparison.Ordinal))
            {
                using StreamReader reader = new(stream);
                string template = await reader.ReadToEndAsync(ct)
                    .ConfigureAwait(false);
                string html = template
                    .Replace(TitleMarker, encodedTitle, StringComparison.Ordinal)
                    .Replace(
                        PresentationMarker,
                        presentationScript,
                        StringComparison.Ordinal)
                    .Replace(DbBlobMarker, blobScript, StringComparison.Ordinal)
                    .Replace(
                        AssetVersionMarker,
                        RendererAssetsVersion,
                        StringComparison.Ordinal);
                await File.WriteAllTextAsync(
                    Path.Combine(subSiteOut, "index.html"),
                    html,
                    ct).ConfigureAwait(false);
            }
            else
            {
                string relative = isDiscussion
                    ? name.Substring(DiscussionPrefix.Length)
                    : name.Substring(SharedPrefix.Length);
                string outFile = Path.Combine(assetsDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
                await using FileStream fs = new(
                    outFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    useAsync: true);
                await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<byte[]> GzipBytesAsync(
        byte[] raw,
        CancellationToken ct)
    {
        using MemoryStream output = new();
        await using (GZipStream gzip = new(
            output,
            CompressionLevel.Optimal,
            leaveOpen: true))
        {
            await gzip.WriteAsync(raw, ct).ConfigureAwait(false);
        }
        return output.ToArray();
    }
}
