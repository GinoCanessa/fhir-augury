using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.Json;

namespace FhirAugury.Tools.TicketSite;

/// <summary>
/// Emits the applying (planner) sub-site under
/// <c>&lt;rootOut&gt;/applying/</c>. The Phase 5 implementation emits the
/// placeholder applying SPA template (no DB blob, no filter scripts);
/// Phase 6 fills it out with the real planner SPA and inlines a trimmed
/// planner DB the same way the discussion side inlines the preparer DB.
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

    public static void Emit(string subSiteOut, string baseTitle, ResolvedFilters filters, byte[] dbBytes)
    {
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
        string base64 = Convert.ToBase64String(GzipBytes(dbBytes));
        string blobScript = $"<script>window.__DB__='{base64}';window.__DBGZ__=1;</script>";
        string filtersScript = BuildFiltersScript(filters);

        foreach (string name in resourceNames)
        {
            bool isApplying = name.StartsWith(ApplyingPrefix, StringComparison.Ordinal);
            bool isShared = name.StartsWith(SharedPrefix, StringComparison.Ordinal);
            if (!isApplying && !isShared) continue;

            using Stream stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing embedded resource: {name}");

            if (string.Equals(name, TemplateName, StringComparison.Ordinal))
            {
                using StreamReader reader = new(stream);
                string template = reader.ReadToEnd();
                string html = template
                    .Replace(TitleMarker, encodedTitle, StringComparison.Ordinal)
                    .Replace(FiltersMarker, filtersScript, StringComparison.Ordinal)
                    .Replace(DbBlobMarker, blobScript, StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(subSiteOut, "index.html"), html);
            }
            else
            {
                string relative = isApplying
                    ? name.Substring(ApplyingPrefix.Length)
                    : name.Substring(SharedPrefix.Length);
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
