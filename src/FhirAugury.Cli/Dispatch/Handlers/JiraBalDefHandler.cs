using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

/// <summary>
/// Handles the <c>jira-baldef</c> verb (Ballot Definition / BALDEF-* read
/// model). Forwards to the Jira source through the orchestrator at
/// <c>/api/v1/jira/baldef</c>.
/// </summary>
public static class JiraBalDefHandler
{
    public static async Task<object> HandleAsync(JiraBalDefRequest request, string orchestratorAddr, CancellationToken ct)
    {
        string url = BuildUrl(request);
        using HttpServiceClient client = new(orchestratorAddr);
        return new { data = await client.GetFromOrchestratorAsync(url, ct) };
    }

    /// <summary>
    /// Builds the orchestrator URL for a <c>jira-baldef</c> request. Extracted
    /// for unit-testing the route/query construction without an HTTP call.
    /// </summary>
    internal static string BuildUrl(JiraBalDefRequest request)
    {
        string action = request.Action.ToLowerInvariant();
        switch (action)
        {
            case "list":
                List<string> q = [];
                if (!string.IsNullOrWhiteSpace(request.Cycle)) q.Add($"cycle={Uri.EscapeDataString(request.Cycle)}");
                if (!string.IsNullOrWhiteSpace(request.Level)) q.Add($"level={Uri.EscapeDataString(request.Level)}");
                if (!string.IsNullOrWhiteSpace(request.WorkGroup)) q.Add($"workGroup={Uri.EscapeDataString(request.WorkGroup)}");
                if (request.Limit is int limit) q.Add($"limit={limit}");
                if (request.Offset is int offset) q.Add($"offset={offset}");
                return "/api/v1/jira/baldef" + ToQuery(q);

            case "get":
                if (string.IsNullOrEmpty(request.Key))
                    throw new ArgumentException("Jira baldef action 'get' requires a key.");
                return $"/api/v1/jira/baldef/{Uri.EscapeDataString(request.Key)}";

            default:
                throw new ArgumentException($"Unknown action: {request.Action}. Valid: list, get");
        }
    }

    private static string ToQuery(List<string> parts)
        => parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
}
