using System.Net;
using System.Reflection;
using System.Text.Json;

namespace FhirAugury.Tools.PreparerSite;

internal static class SiteEmitter
{
    private const string ResourcePrefix = "web-assets/";
    private const string TemplateName = "web-assets/index.template.html";
    private const string TitleMarker = "<!-- __TITLE__ -->";
    private const string DbBlobMarker = "<!-- __DB_BLOB__ -->";
    private const string FiltersMarker = "<!-- __FILTERS__ -->";

    // PERF: Convert.ToBase64String(byte[]) allocates the full string up front
    // (no streaming overload exists). For a 39 MB input expect ~39 MB array +
    // ~52 MB string + a StreamWriter buffer ≈ ~100 MB transient peak. If we
    // later hit a memory ceiling, switch to Convert.ToBase64CharArray in 1 MB
    // chunks (-> 1.33 MB output per chunk).
    internal static void Emit(string outDir, string baseTitle, ResolvedFilters filters, byte[] dbBytes)
    {
        if (Directory.Exists(outDir))
        {
            Directory.Delete(outDir, recursive: true);
        }
        Directory.CreateDirectory(outDir);
        string assetsDir = Path.Combine(outDir, "assets");
        Directory.CreateDirectory(assetsDir);

        Assembly asm = typeof(SiteEmitter).Assembly;
        string[] resourceNames = asm.GetManifestResourceNames();

        string fullTitle = baseTitle + filters.ToTitleSuffix();
        string encodedTitle = WebUtility.HtmlEncode(fullTitle);
        string base64 = Convert.ToBase64String(dbBytes);
        string blobScript = $"<script>window.__DB__='{base64}';</script>";
        string filtersScript = BuildFiltersScript(filters);

        foreach (string name in resourceNames)
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string relative = name.Substring(ResourcePrefix.Length);

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
                File.WriteAllText(Path.Combine(outDir, "index.html"), html);
            }
            else
            {
                string outFile = Path.Combine(assetsDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
                using FileStream fs = File.Create(outFile);
                stream.CopyTo(fs);
            }
        }
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

