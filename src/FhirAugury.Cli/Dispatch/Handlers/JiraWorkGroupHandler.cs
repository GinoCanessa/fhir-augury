using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class JiraWorkGroupHandler
{
    public static async Task<object> HandleAsync(JiraWorkGroupRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);

        string url = request.Action.ToLowerInvariant() switch
        {
            "issues" when !string.IsNullOrEmpty(request.GroupCode) 
                => $"/api/v1/jira/work-groups/{Uri.EscapeDataString(request.GroupCode)}/issues",
            "issues" 
                => "/api/v1/jira/work-groups/issues",
            _ => throw new ArgumentException(
                $"Unknown action: {request.Action}. Valid: issues"),
        };

        JsonElement result = await client.GetFromOrchestratorAsync(url, ct);
        return new { data = result };
    }
}
