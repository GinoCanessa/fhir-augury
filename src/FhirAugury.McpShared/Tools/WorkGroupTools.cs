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
}
