using System.Net;
using System.Reflection;
using System.Text.Json;

namespace FhirAugury.Tools.TicketSite;

/// <summary>
/// Emits the discussion (preparer) sub-site under
/// <c>&lt;rootOut&gt;/discussion/</c>. Identical SPA shape to the original
/// preparer-site SPA; only the resource path prefix moved to
/// <c>web-assets/discussion/</c> and the shared sql.js bytes are pulled
/// from <c>web-assets/shared/</c>.
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

        Assembly asm = typeof(PreparerSubSiteEmitter).Assembly;
        string[] resourceNames = asm.GetManifestResourceNames();

        string fullTitle = baseTitle + filters.ToTitleSuffix();
        string encodedTitle = WebUtility.HtmlEncode(fullTitle);
        string base64 = Convert.ToBase64String(dbBytes);
        string blobScript = $"<script>window.__DB__='{base64}';</script>";
        string filtersScript = BuildFiltersScript(filters);

        foreach (string name in resourceNames)
        {
            bool isDiscussion = name.StartsWith(DiscussionPrefix, StringComparison.Ordinal);
            bool isShared = name.StartsWith(SharedPrefix, StringComparison.Ordinal);
            if (!isDiscussion && !isShared) continue;

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
                string relative = isDiscussion
                    ? name.Substring(DiscussionPrefix.Length)
                    : name.Substring(SharedPrefix.Length);
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
