using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class GitHubReposHandler
{
    public static async Task<object> HandleAsync(GitHubReposRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string action = request.Action.ToLowerInvariant();

        if (action == "list")
            return new { data = await client.GetFromOrchestratorAsync("/api/v1/github/repos", ct) };

        if (string.IsNullOrEmpty(request.Owner) || string.IsNullOrEmpty(request.Name))
            throw new ArgumentException($"GitHub repos action '{action}' requires owner and name.");

        string owner = Uri.EscapeDataString(request.Owner);
        string name = Uri.EscapeDataString(request.Name);

        return action switch
        {
            "tags" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/github/repos/{owner}/{name}/tags", ct) },
            "tag-files" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/github/repos/{owner}/{name}/tags/files", ct) },
            "tag-search" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/github/repos/{owner}/{name}/tags/search", ct) },
            _ => throw new ArgumentException($"Unknown action: {request.Action}. Valid: list, tags, tag-files, tag-search"),
        };
    }
}
