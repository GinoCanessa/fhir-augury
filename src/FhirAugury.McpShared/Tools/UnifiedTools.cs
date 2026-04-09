using System.ComponentModel;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.Text;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class UnifiedTools
{
    [McpServerTool, Description("Get status and statistics of all connected services.")]
    public static async Task<string> GetStats(
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            JsonElement root = await GetJsonAsync(client, "/api/v1/services", cancellationToken);

            StringBuilder sb = new();
            sb.AppendLine("## Services Status");
            sb.AppendLine();

            foreach (JsonElement svc in root.GetProperty("services").EnumerateArray())
            {
                sb.AppendLine($"### {GetString(svc, "name")}");
                sb.AppendLine($"- **Status:** {GetString(svc, "status")}");
                if (svc.TryGetProperty("itemCount", out JsonElement icEl))
                    sb.AppendLine($"- **Items:** {icEl.GetInt32()}");
                if (svc.TryGetProperty("dbSizeBytes", out JsonElement dbEl) && dbEl.GetInt64() > 0)
                    sb.AppendLine($"- **DB Size:** {FormatBytes(dbEl.GetInt64())}");
                AppendIfPresent(sb, svc, "lastSyncAt", "Last Sync");
                AppendIfPresent(sb, svc, "lastError", "Last Error");

                if (svc.TryGetProperty("indexes", out JsonElement idxsEl) && idxsEl.ValueKind == JsonValueKind.Array && idxsEl.GetArrayLength() > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("| Index | Status | Records | Last Rebuilt | Error |");
                    sb.AppendLine("|-------|--------|---------|-------------|-------|");
                    foreach (JsonElement idx in idxsEl.EnumerateArray())
                    {
                        string idxName = GetString(idx, "name");
                        bool rebuilding = idx.TryGetProperty("isRebuilding", out JsonElement rebEl) && rebEl.GetBoolean();
                        int records = idx.TryGetProperty("recordCount", out JsonElement rcEl) ? rcEl.GetInt32() : 0;
                        string lastRebuilt = GetNullableString(idx, "lastRebuildCompletedAt") ?? "—";
                        string error = GetNullableString(idx, "lastError") ?? "—";
                        sb.AppendLine($"| {idxName} | {(rebuilding ? "rebuilding" : "idle")} | {records:N0} | {lastRebuilt} | {error} |");
                    }
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Trigger synchronization/ingestion across source services.")]
    public static async Task<string> TriggerSync(
        IHttpClientFactory httpClientFactory,
        [Description("Comma-separated sources to sync (empty for all)")] string? sources = null,
        [Description("Sync type: incremental, full, rebuild (default incremental)")] string type = "incremental",
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/ingest/trigger?type={Uri.EscapeDataString(type)}");
            if (!string.IsNullOrWhiteSpace(sources))
                url.Append($"&sources={Uri.EscapeDataString(sources)}");

            JsonElement root = await PostJsonAsync(client, url.ToString(), cancellationToken);

            StringBuilder sb = new();
            sb.AppendLine("## Sync Triggered");
            sb.AppendLine();

            foreach (JsonElement status in root.GetProperty("statuses").EnumerateArray())
            {
                sb.AppendLine($"- **{GetString(status, "source")}:** {GetString(status, "status")}");
                string? message = GetNullableString(status, "message");
                if (!string.IsNullOrEmpty(message))
                    sb.AppendLine($"  {message}");
            }

            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Rebuild specific indexes on source services. Use this to refresh BM25 search indexes, FTS5 full-text indexes, cross-reference indexes, or other specialized indexes.")]
    public static async Task<string> RebuildIndex(
        IHttpClientFactory httpClientFactory,
        [Description("Comma-separated sources to rebuild indexes on (empty for all): jira,zulip,github,confluence")] string? sources = null,
        [Description("Index type: all, bm25, fts, cross-refs, lookup-tables, commits, artifact-map, page-links")] string indexType = "all",
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/rebuild-index?type={Uri.EscapeDataString(indexType)}");
            if (!string.IsNullOrWhiteSpace(sources))
                url.Append($"&sources={Uri.EscapeDataString(sources)}");

            JsonElement root = await PostJsonAsync(client, url.ToString(), cancellationToken);

            JsonElement results = root.GetProperty("results");
            StringBuilder sb = new();
            sb.AppendLine($"## Index Rebuild Results ({results.GetArrayLength()} sources)");
            sb.AppendLine();

            foreach (JsonElement status in results.EnumerateArray())
            {
                bool success = status.TryGetProperty("success", out JsonElement sucEl) && sucEl.GetBoolean();
                string icon = success ? "✅" : "❌";
                string detail = success
                    ? GetNullableString(status, "actionTaken") ?? ""
                    : GetNullableString(status, "error") ?? "";
                sb.AppendLine($"- {icon} **{GetString(status, "source")}**: {detail}");
            }

            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ── Shared helpers ───────────────────────────────────────────

    internal static string FormatSearchResults(JsonElement root, string query)
    {
        JsonElement results = root.GetProperty("results");
        int total = root.TryGetProperty("total", out JsonElement totalEl) ? totalEl.GetInt32() : results.GetArrayLength();

        if (results.GetArrayLength() == 0)
            return $"No results found for \"{query}\".";

        StringBuilder sb = new();
        sb.AppendLine($"## Search Results ({total} total, showing {results.GetArrayLength()})");
        sb.AppendLine();

        foreach (JsonElement r in results.EnumerateArray())
        {
            string id = GetNullableString(r, "id") ?? GetNullableString(r, "key") ?? "?";
            string title = GetString(r, "title");
            string? source = GetNullableString(r, "source");

            sb.AppendLine(source is not null ? $"### [{source}] {id} — {title}" : $"### {id} — {title}");

            if (r.TryGetProperty("score", out JsonElement scoreEl) && scoreEl.ValueKind == JsonValueKind.Number)
                sb.AppendLine($"- **Score:** {scoreEl.GetDouble():F2}");
            AppendIfPresent(sb, r, "updatedAt", "Updated");
            AppendIfPresent(sb, r, "status", "Status");
            AppendIfPresent(sb, r, "url", "URL");
            AppendIfPresent(sb, r, "snippet", "Snippet");

            sb.AppendLine();
        }

        if (root.TryGetProperty("warnings", out JsonElement warningsEl)
            && warningsEl.ValueKind == JsonValueKind.Array
            && warningsEl.GetArrayLength() > 0)
        {
            sb.AppendLine("**Warnings:**");
            foreach (JsonElement w in warningsEl.EnumerateArray())
                sb.AppendLine($"- {w.GetString()}");
        }

        return sb.ToString();
    }

    internal static async Task<JsonElement> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using HttpResponseMessage response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    internal static async Task<JsonElement> PostJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using HttpResponseMessage response = await client.PostAsync(url, null, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    internal static async Task<JsonElement> PostJsonBodyAsync(HttpClient client, string url, object body, CancellationToken ct)
    {
        string bodyJson = JsonSerializer.Serialize(body, CamelCaseOptions);
        using StringContent content = new(bodyJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    internal static string GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() ?? ""
            : "";

    internal static string? GetNullableString(JsonElement el, string name) =>
        el.TryGetProperty(name, out JsonElement val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;

    private static void AppendIfPresent(StringBuilder sb, JsonElement el, string prop, string label, string prefix = "- ")
    {
        string? value = GetNullableString(el, prop);
        if (!string.IsNullOrEmpty(value))
            sb.AppendLine($"{prefix}**{label}:** {value}");
    }

    private static string FormatBytes(long bytes) => FormatHelpers.FormatBytes(bytes);

    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
