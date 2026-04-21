using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

[McpServerToolType]
public static class JiraSpecsTools
{
    [McpServerTool, Description("List all FHIR specifications tracked in GitHub.")]
    public static async Task<string> ListJiraSpecs(
        IHttpClientFactory httpClientFactory,
        [Description("Filter by spec family")] string? family = null,
        [Description("Filter by work group")] string? workgroup = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new("/api/v1/github/jira-specs");
            List<string> query = [];
            if (family != null) query.Add($"family={Uri.EscapeDataString(family)}");
            if (workgroup != null) query.Add($"workgroup={Uri.EscapeDataString(workgroup)}");
            if (query.Count > 0) url.Append($"?{string.Join('&', query)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get details of a specific FHIR specification.")]
    public static async Task<string> GetJiraSpec(
        IHttpClientFactory httpClientFactory,
        [Description("Specification key")] string specKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/github/jira-specs/{Uri.EscapeDataString(specKey)}";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get artifacts (resources, profiles, etc.) for a FHIR specification.")]
    public static async Task<string> GetJiraSpecArtifacts(
        IHttpClientFactory httpClientFactory,
        [Description("Specification key")] string specKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/github/jira-specs/{Uri.EscapeDataString(specKey)}/artifacts";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get specification pages for a FHIR specification.")]
    public static async Task<string> GetJiraSpecPages(
        IHttpClientFactory httpClientFactory,
        [Description("Specification key")] string specKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/github/jira-specs/{Uri.EscapeDataString(specKey)}/pages";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get version history for a FHIR specification.")]
    public static async Task<string> GetJiraSpecVersions(
        IHttpClientFactory httpClientFactory,
        [Description("Specification key")] string specKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string url = $"/api/v1/github/jira-specs/{Uri.EscapeDataString(specKey)}/versions";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Resolve a FHIR artifact key to its specification and URL.")]
    public static async Task<string> ResolveJiraSpecArtifact(
        IHttpClientFactory httpClientFactory,
        [Description("Artifact key (e.g., Patient, Observation)")] string artifactKey,
        [Description("Optional spec key to scope resolution")] string? specKey = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/github/jira-specs/resolve-artifact/{Uri.EscapeDataString(artifactKey)}");
            if (specKey != null) url.Append($"?specKey={Uri.EscapeDataString(specKey)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Resolve a FHIR specification page key to its spec and URL.")]
    public static async Task<string> ResolveJiraSpecPage(
        IHttpClientFactory httpClientFactory,
        [Description("Page key")] string pageKey,
        [Description("Optional spec key to scope resolution")] string? specKey = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            StringBuilder url = new($"/api/v1/github/jira-specs/resolve-page/{Uri.EscapeDataString(pageKey)}");
            if (specKey != null) url.Append($"?specKey={Uri.EscapeDataString(specKey)}");

            JsonElement root = await UnifiedTools.GetJsonAsync(client, url.ToString(), cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List all FHIR specification work groups.")]
    public static async Task<string> ListJiraSpecWorkgroups(
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            JsonElement root = await UnifiedTools.GetJsonAsync(client, "/api/v1/github/jira-specs/workgroups", cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("List all FHIR specification families.")]
    public static async Task<string> ListJiraSpecFamilies(
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            JsonElement root = await UnifiedTools.GetJsonAsync(client, "/api/v1/github/jira-specs/families", cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a FHIR specification by its Git URL.")]
    public static async Task<string> GetJiraSpecByGitUrl(
        IHttpClientFactory httpClientFactory,
        [Description("Git repository URL")] string url,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string requestUrl = $"/api/v1/github/jira-specs/by-git-url?url={Uri.EscapeDataString(url)}";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, requestUrl, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get a FHIR specification by its canonical URL.")]
    public static async Task<string> GetJiraSpecByCanonical(
        IHttpClientFactory httpClientFactory,
        [Description("Canonical URL")] string url,
        CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            string requestUrl = $"/api/v1/github/jira-specs/by-canonical?url={Uri.EscapeDataString(url)}";

            JsonElement root = await UnifiedTools.GetJsonAsync(client, requestUrl, cancellationToken);
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
