using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class JiraLocalProcessingHandler
{
    private static readonly HashSet<string> ValidActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "tickets", "random-ticket", "set-processed", "clear-all-processed",
    };

    public static async Task<object> HandleAsync(JiraLocalProcessingRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string action = request.Action.ToLowerInvariant();

        if (!ValidActions.Contains(action))
            throw new ArgumentException($"Unknown action: {request.Action}. Valid: {string.Join(", ", ValidActions)}");

        string url = $"/api/v1/jira/local-processing/{action}";
        string? body = request.Body.HasValue ? JsonSerializer.Serialize(request.Body.Value) : null;
        JsonElement result = await client.PostToOrchestratorAsync(url, body, ct);
        return new { data = result };
    }
}
