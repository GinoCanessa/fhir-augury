using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class ContentTools
{
    [McpServerTool, Description("Search across all FHIR community sources using multi-value content search.")]
    public static async Task<string> ContentSearch(
        IHttpClientFactory httpClientFactory,
        [Description("One or more search values/terms (comma-separated or array)")] string values,
        [Description("Comma-separated source filter: zulip,jira,confluence,github")] string? sources = null,
        [Description("Maximum results to return (default 20)")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new("/api/v1/content/search?");

            foreach (string v in ParseCsv(values))
                url.Append($"values={Uri.EscapeDataString(v)}&");

            if (!string.IsNullOrWhiteSpace(sources))
            {
                foreach (string s in ParseCsv(sources))
                    url.Append($"sources={Uri.EscapeDataString(s)}&");
            }

            url.Append($"limit={limit}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatContentSearchResults(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Find what a specific item refers to (outgoing cross-references).")]
    public static async Task<string> RefersTo(
        IHttpClientFactory httpClientFactory,
        [Description("Value to look up (e.g., FHIR-50783, owner/repo#42, Patient.birthDate)")] string value,
        [Description("Filter by target source type: jira, zulip, github, confluence, fhir")] string? sourceType = null,
        [Description("Maximum results (default 50)")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return await XRefQuery(httpClientFactory, "refers-to", value, sourceType, limit, cancellationToken);
    }

    [McpServerTool, Description("Find what refers to a specific item (incoming cross-references).")]
    public static async Task<string> ReferredBy(
        IHttpClientFactory httpClientFactory,
        [Description("Value to look up (e.g., FHIR-50783, owner/repo#42, Patient.birthDate)")] string value,
        [Description("Filter by source type: jira, zulip, github, confluence, fhir")] string? sourceType = null,
        [Description("Maximum results (default 50)")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return await XRefQuery(httpClientFactory, "referred-by", value, sourceType, limit, cancellationToken);
    }

    [McpServerTool, Description("Find all cross-references for a value (both incoming and outgoing).")]
    public static async Task<string> CrossReferenced(
        IHttpClientFactory httpClientFactory,
        [Description("Value to look up (e.g., FHIR-50783, owner/repo#42, Patient.birthDate)")] string value,
        [Description("Filter by source type: jira, zulip, github, confluence, fhir")] string? sourceType = null,
        [Description("Maximum results (default 50)")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return await XRefQuery(httpClientFactory, "cross-referenced", value, sourceType, limit, cancellationToken);
    }

    [McpServerTool, Description("Get full details of a content item from any source, with optional content body, comments, and markdown snapshot.")]
    public static async Task<string> GetItem(
        IHttpClientFactory httpClientFactory,
        [Description("Source system: jira, zulip, github, confluence")] string source,
        [Description("Item identifier (e.g., FHIR-50783, 12345, owner/repo#42)")] string id,
        [Description("Include full content body")] bool includeContent = false,
        [Description("Include comments")] bool includeComments = false,
        [Description("Include markdown snapshot")] bool includeSnapshot = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string encodedId = source.Equals("github", StringComparison.OrdinalIgnoreCase)
                ? id.Replace("#", "%23")
                : Uri.EscapeDataString(id);

            StringBuilder url = new($"/api/v1/content/item/{Uri.EscapeDataString(source)}/{encodedId}?");
            if (includeContent) url.Append("includeContent=true&");
            if (includeComments) url.Append("includeComments=true&");
            if (includeSnapshot) url.Append("includeSnapshot=true&");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString().TrimEnd('&', '?'), cancellationToken);
            return FormatItemResponse(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ── Shared helpers ───────────────────────────────────────────────────

    private static async Task<string> XRefQuery(
        IHttpClientFactory httpClientFactory, string direction, string value,
        string? sourceType, int limit, CancellationToken cancellationToken)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/content/{direction}?value={Uri.EscapeDataString(value)}&limit={limit}");
            if (!string.IsNullOrWhiteSpace(sourceType))
                url.Append($"&sourceType={Uri.EscapeDataString(sourceType)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatXRefResponse(root, direction, value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static string FormatXRefResponse(JsonElement root, string direction, string value)
    {
        JsonElement hits = root.GetProperty("hits");
        int total = root.TryGetProperty("total", out JsonElement totalEl) ? totalEl.GetInt32() : hits.GetArrayLength();

        if (hits.GetArrayLength() == 0)
            return $"No {direction} references found for \"{value}\".";

        StringBuilder sb = new();
        sb.AppendLine($"## {FormatDirection(direction)} for \"{value}\" ({total} results)");
        sb.AppendLine();

        foreach (JsonElement hit in hits.EnumerateArray())
        {
            string srcType = UnifiedTools.GetString(hit, "sourceType");
            string srcId = UnifiedTools.GetString(hit, "sourceId");
            string tgtType = UnifiedTools.GetString(hit, "targetType");
            string tgtId = UnifiedTools.GetString(hit, "targetId");

            sb.AppendLine($"- [{srcType}] {srcId} → [{tgtType}] {tgtId}");

            string? srcTitle = UnifiedTools.GetNullableString(hit, "sourceTitle");
            if (!string.IsNullOrEmpty(srcTitle))
                sb.AppendLine($"  **Source:** {srcTitle}");

            string? tgtTitle = UnifiedTools.GetNullableString(hit, "targetTitle");
            if (!string.IsNullOrEmpty(tgtTitle))
                sb.AppendLine($"  **Target:** {tgtTitle}");

            string? linkType = UnifiedTools.GetNullableString(hit, "linkType");
            if (!string.IsNullOrEmpty(linkType))
                sb.AppendLine($"  **Type:** {linkType}");

            string? context = UnifiedTools.GetNullableString(hit, "context");
            if (!string.IsNullOrEmpty(context))
                sb.AppendLine($"  **Context:** {context}");
        }

        AppendWarnings(sb, root);
        return sb.ToString();
    }

    private static string FormatContentSearchResults(JsonElement root)
    {
        JsonElement hits = root.GetProperty("hits");
        int total = root.TryGetProperty("total", out JsonElement totalEl) ? totalEl.GetInt32() : hits.GetArrayLength();

        if (hits.GetArrayLength() == 0)
            return "No results found.";

        StringBuilder sb = new();
        sb.AppendLine($"## Search Results ({total} total, showing {hits.GetArrayLength()})");
        sb.AppendLine();

        foreach (JsonElement h in hits.EnumerateArray())
        {
            string id = UnifiedTools.GetString(h, "id");
            string title = UnifiedTools.GetString(h, "title");
            string? source = UnifiedTools.GetNullableString(h, "source");

            sb.AppendLine(source is not null ? $"### [{source}] {id} — {title}" : $"### {id} — {title}");

            if (h.TryGetProperty("score", out JsonElement scoreEl) && scoreEl.ValueKind == JsonValueKind.Number)
                sb.AppendLine($"- **Score:** {scoreEl.GetDouble():F2}");

            string? matchedValue = UnifiedTools.GetNullableString(h, "matchedValue");
            if (!string.IsNullOrEmpty(matchedValue))
                sb.AppendLine($"- **Matched:** {matchedValue}");

            string? updatedAt = UnifiedTools.GetNullableString(h, "updatedAt");
            if (!string.IsNullOrEmpty(updatedAt))
                sb.AppendLine($"- **Updated:** {updatedAt}");

            string? url = UnifiedTools.GetNullableString(h, "url");
            if (!string.IsNullOrEmpty(url))
                sb.AppendLine($"- **URL:** {url}");

            string? snippet = UnifiedTools.GetNullableString(h, "snippet");
            if (!string.IsNullOrEmpty(snippet))
                sb.AppendLine($"- **Snippet:** {snippet}");

            sb.AppendLine();
        }

        AppendWarnings(sb, root);
        return sb.ToString();
    }

    private static string FormatItemResponse(JsonElement root)
    {
        string id = UnifiedTools.GetNullableString(root, "id") ?? "?";
        string title = UnifiedTools.GetString(root, "title");
        string? source = UnifiedTools.GetNullableString(root, "source");

        StringBuilder sb = new();
        sb.AppendLine($"# {(source is not null ? $"[{source}] " : "")}{id}: {title}");
        sb.AppendLine();

        if (root.TryGetProperty("metadata", out JsonElement metaEl) && metaEl.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty kv in metaEl.EnumerateObject())
                sb.AppendLine($"- **{kv.Name}:** {kv.Value.GetString()}");
        }

        string? createdAt = UnifiedTools.GetNullableString(root, "createdAt");
        if (!string.IsNullOrEmpty(createdAt))
            sb.AppendLine($"- **Created:** {createdAt}");

        string? updatedAt = UnifiedTools.GetNullableString(root, "updatedAt");
        if (!string.IsNullOrEmpty(updatedAt))
            sb.AppendLine($"- **Updated:** {updatedAt}");

        string? url = UnifiedTools.GetNullableString(root, "url");
        if (!string.IsNullOrEmpty(url))
            sb.AppendLine($"- **URL:** {url}");

        string? content = UnifiedTools.GetNullableString(root, "content");
        if (!string.IsNullOrEmpty(content))
        {
            sb.AppendLine();
            sb.AppendLine("## Content");
            sb.AppendLine(content);
        }

        if (root.TryGetProperty("comments", out JsonElement commentsEl)
            && commentsEl.ValueKind == JsonValueKind.Array && commentsEl.GetArrayLength() > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Comments ({commentsEl.GetArrayLength()})");
            sb.AppendLine();
            foreach (JsonElement c in commentsEl.EnumerateArray())
            {
                string author = UnifiedTools.GetString(c, "author");
                string? date = UnifiedTools.GetNullableString(c, "createdAt");
                sb.AppendLine($"### {author} — {date}");
                sb.AppendLine(UnifiedTools.GetString(c, "body"));
                sb.AppendLine();
            }
        }

        string? snapshot = UnifiedTools.GetNullableString(root, "snapshot");
        if (!string.IsNullOrEmpty(snapshot))
        {
            sb.AppendLine();
            sb.AppendLine("## Snapshot");
            sb.AppendLine(snapshot);
        }

        return sb.ToString();
    }

    private static void AppendWarnings(StringBuilder sb, JsonElement root)
    {
        if (root.TryGetProperty("warnings", out JsonElement warningsEl)
            && warningsEl.ValueKind == JsonValueKind.Array
            && warningsEl.GetArrayLength() > 0)
        {
            sb.AppendLine("**Warnings:**");
            foreach (JsonElement w in warningsEl.EnumerateArray())
                sb.AppendLine($"- {w.GetString()}");
        }
    }

    private static string FormatDirection(string direction) => direction switch
    {
        "refers-to" => "Refers To",
        "referred-by" => "Referred By",
        "cross-referenced" => "Cross-References",
        _ => direction,
    };

    private static List<string> ParseCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
