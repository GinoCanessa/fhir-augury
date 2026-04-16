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
            string encodedId = Uri.EscapeDataString(id);

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

    [McpServerTool, Description("Get extracted keywords for a specific item, sorted by BM25 relevance score. " +
        "Shows keyword text, classification (word, fhir_path, fhir_operation), occurrence count, and BM25 score.")]
    public static async Task<string> GetKeywords(
        IHttpClientFactory httpClientFactory,
        [Description("Source system (e.g., github, jira, zulip, confluence)")] string source,
        [Description("Item ID within the source (e.g., HL7/fhir#4006, FHIR-55001)")] string id,
        [Description("Filter by keyword type: word, fhir_path, fhir_operation. Omit for all types.")] string? keywordType = null,
        [Description("Maximum keywords to return (default 50)")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        HttpClient client = httpClientFactory.CreateClient("orchestrator");
        string encodedId = Uri.EscapeDataString(id);
        string url = $"/api/v1/content/keywords/{Uri.EscapeDataString(source)}/{encodedId}";
        List<string> queryParts = [];
        if (!string.IsNullOrEmpty(keywordType)) queryParts.Add($"keywordType={Uri.EscapeDataString(keywordType)}");
        if (limit.HasValue) queryParts.Add($"limit={limit.Value}");
        if (queryParts.Count > 0) url += "?" + string.Join("&", queryParts);

        JsonElement json = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);

        string sourceVal = UnifiedTools.GetString(json, "source");
        string sourceId = UnifiedTools.GetString(json, "sourceId");
        string contentType = UnifiedTools.GetString(json, "contentType");

        StringBuilder sb = new();
        sb.AppendLine($"## Keywords for {sourceVal}:{sourceId}");
        if (!string.IsNullOrEmpty(contentType))
            sb.AppendLine($"Content type: {contentType}");
        sb.AppendLine();

        if (json.TryGetProperty("keywords", out JsonElement keywords) && keywords.GetArrayLength() > 0)
        {
            sb.AppendLine("| Keyword | Type | Count | BM25 Score |");
            sb.AppendLine("|---------|------|------:|------------|");
            foreach (JsonElement kw in keywords.EnumerateArray())
            {
                string keyword = UnifiedTools.GetString(kw, "keyword");
                string kwType = UnifiedTools.GetString(kw, "keywordType");
                int count = kw.TryGetProperty("count", out JsonElement c) ? c.GetInt32() : 0;
                double score = kw.TryGetProperty("bm25Score", out JsonElement s) ? s.GetDouble() : 0;
                sb.AppendLine($"| {keyword} | {kwType} | {count} | {score:F3} |");
            }
        }
        else
        {
            sb.AppendLine("No keywords found for this item.");
        }

        return sb.ToString();
    }

    [McpServerTool, Description("Find items related to a given item by shared keyword similarity. " +
        "Returns items ranked by BM25 keyword vector similarity, with shared keywords listed.")]
    public static async Task<string> RelatedByKeyword(
        IHttpClientFactory httpClientFactory,
        [Description("Source system (e.g., github, jira, zulip, confluence)")] string source,
        [Description("Item ID within the source (e.g., HL7/fhir#4006, FHIR-55001)")] string id,
        [Description("Minimum similarity score threshold 0.0-1.0 (default 0.1)")] double? minScore = null,
        [Description("Filter by keyword type: word, fhir_path, fhir_operation")] string? keywordType = null,
        [Description("Maximum related items to return (default 20)")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        HttpClient client = httpClientFactory.CreateClient("orchestrator");
        string encodedId = Uri.EscapeDataString(id);
        string url = $"/api/v1/content/related-by-keyword/{Uri.EscapeDataString(source)}/{encodedId}";
        List<string> queryParts = [];
        if (minScore.HasValue) queryParts.Add($"minScore={minScore.Value}");
        if (!string.IsNullOrEmpty(keywordType)) queryParts.Add($"keywordType={Uri.EscapeDataString(keywordType)}");
        if (limit.HasValue) queryParts.Add($"limit={limit.Value}");
        if (queryParts.Count > 0) url += "?" + string.Join("&", queryParts);

        JsonElement json = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);

        string sourceVal = UnifiedTools.GetString(json, "source");
        string sourceId = UnifiedTools.GetString(json, "sourceId");

        StringBuilder sb = new();
        sb.AppendLine($"## Items Related to {sourceVal}:{sourceId} (by keyword similarity)");
        sb.AppendLine();

        if (json.TryGetProperty("relatedItems", out JsonElement items) && items.GetArrayLength() > 0)
        {
            sb.AppendLine("| Score | Source | ID | Title | Shared Keywords |");
            sb.AppendLine("|------:|--------|-------|-------|-----------------|");
            foreach (JsonElement item in items.EnumerateArray())
            {
                string itemSource = UnifiedTools.GetString(item, "source");
                string itemId = UnifiedTools.GetString(item, "sourceId");
                string title = UnifiedTools.GetString(item, "title");
                double score = item.TryGetProperty("score", out JsonElement s) ? s.GetDouble() : 0;
                List<string> sharedKws = [];
                if (item.TryGetProperty("sharedKeywords", out JsonElement skws) && skws.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement skw in skws.EnumerateArray())
                        sharedKws.Add(skw.GetString() ?? "");
                }
                string kwStr = string.Join(", ", sharedKws);
                sb.AppendLine($"| {score:F3} | {itemSource} | {itemId} | {title} | {kwStr} |");
            }
        }
        else
        {
            sb.AppendLine("No related items found.");
        }

        if (json.TryGetProperty("warnings", out JsonElement warnings) && warnings.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine();
            foreach (JsonElement w in warnings.EnumerateArray())
                sb.AppendLine($"⚠️ {w.GetString()}");
        }

        return sb.ToString();
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

            if (hit.TryGetProperty("score", out JsonElement scoreEl) && scoreEl.ValueKind == JsonValueKind.Number)
                sb.AppendLine($"  **Score:** {scoreEl.GetDouble():F2}");

            if (hit.TryGetProperty("updatedAt", out JsonElement updatedEl) && updatedEl.ValueKind == JsonValueKind.String)
            {
                string? updatedStr = updatedEl.GetString();
                if (!string.IsNullOrEmpty(updatedStr))
                    sb.AppendLine($"  **Updated:** {updatedStr}");
            }
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
