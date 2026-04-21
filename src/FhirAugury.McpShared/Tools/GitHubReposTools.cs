using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class GitHubReposTools
{
    [McpServerTool, Description("List all configured GitHub repositories.")]
    public static async Task<string> ListGitHubRepos(
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            JsonElement root = await UnifiedTools.GetJsonAsync(client, "/api/v1/github/repos", cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List tags for a GitHub repository.")]
    public static async Task<string> ListGitHubRepoTags(
        IHttpClientFactory httpClientFactory,
        [Description("Repository owner")] string owner,
        [Description("Repository name")] string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/github/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/tags";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List tag files for a GitHub repository.")]
    public static async Task<string> ListGitHubRepoTagFiles(
        IHttpClientFactory httpClientFactory,
        [Description("Repository owner")] string owner,
        [Description("Repository name")] string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/github/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/tags/files";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Search tags in a GitHub repository.")]
    public static async Task<string> SearchGitHubRepoTags(
        IHttpClientFactory httpClientFactory,
        [Description("Repository owner")] string owner,
        [Description("Repository name")] string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/github/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/tags/search";

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
