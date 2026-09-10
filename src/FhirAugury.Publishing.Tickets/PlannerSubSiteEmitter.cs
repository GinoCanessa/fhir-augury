using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.Json;
using FhirAugury.Common.IO;

namespace FhirAugury.Publishing.Tickets;

/// <summary>
/// Emits the applying (planner) sub-site under
/// <c>&lt;rootOut&gt;/applying/</c> from a validated, optionally trimmed
/// immutable Planner snapshot.
/// </summary>
internal static class PlannerSubSiteEmitter
{
    public const string SubSiteFolder = "applying";
    public const string Kind = "planner";

    private const string ApplyingPrefix = "web-assets/applying/";
    private const string SharedPrefix = "web-assets/shared/";
    private const string TemplateName = "web-assets/applying/index.template.html";
    private const string TitleMarker = "<!-- __TITLE__ -->";
    private const string DbBlobMarker = "<!-- __DB_BLOB__ -->";
    private const string FiltersMarker = "<!-- __FILTERS__ -->";
    private const string AssetVersionMarker = "__ASSET_VERSION__";

    public static string RendererAssetsVersion =>
        StagedDirectoryPublisher.GetRendererAssetsVersion(
            typeof(PlannerSubSiteEmitter).Assembly);

    public static async Task EmitAsync(
        string subSiteOut,
        string baseTitle,
        ResolvedFilters filters,
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

        Assembly asm = typeof(PlannerSubSiteEmitter).Assembly;
        string[] resourceNames = asm.GetManifestResourceNames();

        string fullTitle = baseTitle + filters.ToTitleSuffix();
        string encodedTitle = WebUtility.HtmlEncode(fullTitle);
        byte[] compressed =
            await GzipBytesAsync(dbBytes, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        string base64 = Convert.ToBase64String(compressed);
        ct.ThrowIfCancellationRequested();
        string blobScript = $"<script>window.__DB__='{base64}';window.__DBGZ__=1;</script>";
        string filtersScript = BuildFiltersScript(filters);

        foreach (string name in resourceNames)
        {
            ct.ThrowIfCancellationRequested();
            bool isApplying = name.StartsWith(ApplyingPrefix, StringComparison.Ordinal);
            bool isShared = name.StartsWith(SharedPrefix, StringComparison.Ordinal);
            if (!isApplying && !isShared) continue;

            using Stream stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing embedded resource: {name}");

            if (string.Equals(name, TemplateName, StringComparison.Ordinal))
            {
                using StreamReader reader = new(stream);
                string template = await reader.ReadToEndAsync(ct)
                    .ConfigureAwait(false);
                string html = template
                    .Replace(TitleMarker, encodedTitle, StringComparison.Ordinal)
                    .Replace(FiltersMarker, filtersScript, StringComparison.Ordinal)
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
                string relative = isApplying
                    ? name.Substring(ApplyingPrefix.Length)
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

    private static string BuildFiltersScript(ResolvedFilters filters)
    {
        Dictionary<string, string> map = [];
        if (filters.Specification is not null) map["spec"] = filters.Specification;
        if (filters.Project is not null) map["project"] = filters.Project;
        if (filters.WorkGroup is not null) map["wg"] = filters.WorkGroup;
        string json = JsonSerializer.Serialize(map);
        return $"<script>window.__FILTERS__={json};</script>";
    }
}
