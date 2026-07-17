using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class JiraProjectHandler
{
    public static async Task<object> HandleAsync(JiraProjectRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string action = request.Action.ToLowerInvariant();

        if (action == "list")
            return new { data = await client.GetFromOrchestratorAsync("/api/v1/jira/projects", ct) };

        if (string.IsNullOrEmpty(request.Key))
            throw new ArgumentException($"Jira project action '{action}' requires a key.");

        string key = Uri.EscapeDataString(request.Key);
        return action switch
        {
            "get" => new { data = await client.GetFromOrchestratorAsync($"/api/v1/jira/projects/{key}", ct) },
            "update" => new
            {
                data = await client.PutToOrchestratorAsync(
                    $"/api/v1/jira/projects/{key}",
                    request.Body.HasValue ? JsonSerializer.Serialize(request.Body.Value) : null,
                    ct),
            },
            _ => throw new ArgumentException($"Unknown action: {request.Action}. Valid: list, get, update"),
        };
    }
}
