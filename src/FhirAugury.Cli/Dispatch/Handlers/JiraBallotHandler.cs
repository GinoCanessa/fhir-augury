using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

/// <summary>
/// Handles the <c>jira-ballot</c> verb (Ballot vote / BALLOT-* read model).
/// Forwards to the Jira source through the orchestrator at
/// <c>/api/v1/jira/ballot</c>.
/// </summary>
public static class JiraBallotHandler
{
    public static async Task<object> HandleAsync(JiraBallotRequest request, string orchestratorAddr, CancellationToken ct)
    {
        string url = BuildUrl(request);
        using HttpServiceClient client = new(orchestratorAddr);
        return new { data = await client.GetFromOrchestratorAsync(url, ct) };
    }

    /// <summary>
    /// Builds the orchestrator URL for a <c>jira-ballot</c> request. Extracted
    /// for unit-testing the route/query construction without an HTTP call.
    /// </summary>
    internal static string BuildUrl(JiraBallotRequest request)
    {
        string action = request.Action.ToLowerInvariant();
        switch (action)
        {
            case "list":
                List<string> q = [];
                if (!string.IsNullOrWhiteSpace(request.Cycle)) q.Add($"cycle={Uri.EscapeDataString(request.Cycle)}");
                if (!string.IsNullOrWhiteSpace(request.Specification)) q.Add($"specification={Uri.EscapeDataString(request.Specification)}");
                if (!string.IsNullOrWhiteSpace(request.Disposition)) q.Add($"disposition={Uri.EscapeDataString(request.Disposition)}");
                if (request.Limit is int limit) q.Add($"limit={limit}");
                if (request.Offset is int offset) q.Add($"offset={offset}");
                return "/api/v1/jira/ballot" + ToQuery(q);

            case "get":
                if (string.IsNullOrEmpty(request.Key))
                    throw new ArgumentException("Jira ballot action 'get' requires a key.");
                return $"/api/v1/jira/ballot/{Uri.EscapeDataString(request.Key)}";

            default:
                throw new ArgumentException($"Unknown action: {request.Action}. Valid: list, get");
        }
    }

    private static string ToQuery(List<string> parts)
        => parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
}
