using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class JiraSpecsHandler
{
    private const string Base = "/api/v1/github/jira-specs";

    public static async Task<object> HandleAsync(JiraSpecsRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string action = request.Action.ToLowerInvariant();

        return action switch
        {
            "list" => new { data = await client.GetFromOrchestratorAsync(BuildList(request), ct) },
            "workgroups" => new { data = await client.GetFromOrchestratorAsync($"{Base}/workgroups", ct) },
            "families" => new { data = await client.GetFromOrchestratorAsync($"{Base}/families", ct) },
            "by-git-url" => new { data = await client.GetFromOrchestratorAsync($"{Base}/by-git-url?url={Uri.EscapeDataString(Require(request.Url, "url"))}", ct) },
            "by-canonical" => new { data = await client.GetFromOrchestratorAsync($"{Base}/by-canonical?url={Uri.EscapeDataString(Require(request.Url, "url"))}", ct) },
            "resolve-artifact" => new { data = await client.GetFromOrchestratorAsync(BuildResolveArtifact(request), ct) },
            "resolve-page" => new { data = await client.GetFromOrchestratorAsync(BuildResolvePage(request), ct) },
            "get" => new { data = await client.GetFromOrchestratorAsync($"{Base}/{Uri.EscapeDataString(Require(request.SpecKey, "specKey"))}", ct) },
            "artifacts" => new { data = await client.GetFromOrchestratorAsync($"{Base}/{Uri.EscapeDataString(Require(request.SpecKey, "specKey"))}/artifacts", ct) },
            "pages" => new { data = await client.GetFromOrchestratorAsync($"{Base}/{Uri.EscapeDataString(Require(request.SpecKey, "specKey"))}/pages", ct) },
            "versions" => new { data = await client.GetFromOrchestratorAsync($"{Base}/{Uri.EscapeDataString(Require(request.SpecKey, "specKey"))}/versions", ct) },
            _ => throw new ArgumentException($"Unknown action: {request.Action}. Valid: list, get, artifacts, pages, versions, resolve-artifact, resolve-page, workgroups, families, by-git-url, by-canonical"),
        };
    }

    private static string BuildList(JiraSpecsRequest request)
    {
        List<string> q = [];
        if (!string.IsNullOrEmpty(request.Family)) q.Add($"family={Uri.EscapeDataString(request.Family)}");
        if (!string.IsNullOrEmpty(request.Workgroup)) q.Add($"workgroup={Uri.EscapeDataString(request.Workgroup)}");
        return q.Count > 0 ? $"{Base}?{string.Join('&', q)}" : Base;
    }

    private static string BuildResolveArtifact(JiraSpecsRequest request)
    {
        string artifactKey = Uri.EscapeDataString(Require(request.ArtifactKey, "artifactKey"));
        string url = $"{Base}/resolve-artifact/{artifactKey}";
        if (!string.IsNullOrEmpty(request.SpecKey))
            url += $"?specKey={Uri.EscapeDataString(request.SpecKey)}";
        return url;
    }

    private static string BuildResolvePage(JiraSpecsRequest request)
    {
        string pageKey = Uri.EscapeDataString(Require(request.PageKey, "pageKey"));
        string url = $"{Base}/resolve-page/{pageKey}";
        if (!string.IsNullOrEmpty(request.SpecKey))
            url += $"?specKey={Uri.EscapeDataString(request.SpecKey)}";
        return url;
    }

    private static string Require(string? value, string name) =>
        string.IsNullOrEmpty(value) ? throw new ArgumentException($"{name} is required") : value;
}
