using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class WorkGroupTools
{
    [McpServerTool(Name = "github_workgroup_for_path"),
     Description("Resolve the canonical HL7 work-group attribution for a file path within a GitHub repository. Returns the work-group code, original raw value, and which resolution stage matched (exact-file | directory-prefix | artifact | repo-default | none).")]
    public static async Task<string> GitHubWorkGroupForPath(
        IHttpClientFactory httpClientFactory,
        [Description("Repository full name, e.g. \"HL7/fhir\".")] string repo,
        [Description("Repository-relative file path, forward slashes, e.g. \"source/observation/observation-introduction.md\".")] string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/github/workgroups/resolve?repo={Uri.EscapeDataString(repo)}&path={Uri.EscapeDataString(path)}";
            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List canonical HL7 work-groups with per-repo coverage counts (served by the GitHub source).")]
    public static async Task<string> ListGitHubWorkGroups(
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            JsonElement root = await UnifiedTools.GetJsonAsync(client, "/api/v1/github/workgroups", cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List spec_file_map rows attributed to a canonical work-group within a GitHub repository.")]
    public static async Task<string> ListGitHubWorkGroupFiles(
        IHttpClientFactory httpClientFactory,
        [Description("Repository full name, e.g. \"HL7/fhir\".")] string repo,
        [Description("Canonical work-group code, e.g. \"fhir-i\".")] string workgroup,
        [Description("Maximum number of rows")] int? limit = null,
        [Description("Number of rows to skip")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            List<string> q =
            [
                $"repo={Uri.EscapeDataString(repo)}",
                $"workgroup={Uri.EscapeDataString(workgroup)}",
            ];
            if (limit != null) q.Add($"limit={limit.Value}");
            if (offset != null) q.Add($"offset={offset.Value}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, "/api/v1/github/workgroups/files?" + string.Join('&', q), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List artifacts attributed to a canonical work-group within a GitHub repository.")]
    public static async Task<string> ListGitHubWorkGroupArtifacts(
        IHttpClientFactory httpClientFactory,
        [Description("Repository full name, e.g. \"HL7/fhir\".")] string repo,
        [Description("Canonical work-group code, e.g. \"fhir-i\".")] string workgroup,
        [Description("Maximum number of rows")] int? limit = null,
        [Description("Number of rows to skip")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            List<string> q =
            [
                $"repo={Uri.EscapeDataString(repo)}",
                $"workgroup={Uri.EscapeDataString(workgroup)}",
            ];
            if (limit != null) q.Add($"limit={limit.Value}");
            if (offset != null) q.Add($"offset={offset.Value}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, "/api/v1/github/workgroups/artifacts?" + string.Join('&', q), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List unresolved WorkGroupRaw values that need codeset review (optionally scoped to a repo).")]
    public static async Task<string> ListGitHubWorkGroupUnresolved(
        IHttpClientFactory httpClientFactory,
        [Description("Optional repository full name filter, e.g. \"HL7/fhir\".")] string? repo = null,
        [Description("Maximum number of rows")] int? limit = null,
        [Description("Number of rows to skip")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            List<string> q = [];
            if (repo != null) q.Add($"repo={Uri.EscapeDataString(repo)}");
            if (limit != null) q.Add($"limit={limit.Value}");
            if (offset != null) q.Add($"offset={offset.Value}");
            string url = "/api/v1/github/workgroups/unresolved" + (q.Count == 0 ? string.Empty : "?" + string.Join('&', q));

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static string FormatJson(JsonElement root) =>
        $"```json\n{JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true })}\n```";
}
