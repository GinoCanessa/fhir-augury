using System.Text;
using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class JiraDimensionHandler
{
    public static async Task<object> HandleAsync(JiraDimensionRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);

        return request.Action.ToLowerInvariant() switch
        {
            "users" => await GetAsync(client, "/api/v1/jira/users", ct),
            "inpersons" => await GetAsync(client, "/api/v1/jira/inpersons", ct),
            "issue-numbers" => await GetIssueNumbersAsync(client, request.Project, ct),
            "specification" => await GetSpecificationIssuesAsync(client, request.Spec, request.Limit, request.Offset, ct),
            _ => throw new ArgumentException(
                $"Unknown action: {request.Action}. Valid: users, inpersons, issue-numbers, specification"),
        };
    }

    private static async Task<object> GetAsync(HttpServiceClient client, string url, CancellationToken ct)
    {
        JsonElement result = await client.GetFromOrchestratorAsync(url, ct);
        return new { data = result };
    }

    private static async Task<object> GetIssueNumbersAsync(HttpServiceClient client, string? project, CancellationToken ct)
    {
        string url = "/api/v1/jira/issue-numbers";
        if (project != null) url += $"?project={Uri.EscapeDataString(project)}";
        JsonElement result = await client.GetFromOrchestratorAsync(url, ct);
        return new { data = result };
    }

    private static async Task<object> GetSpecificationIssuesAsync(HttpServiceClient client, string? spec, int? limit, int? offset, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(spec))
            throw new ArgumentException("spec is required for specification action");

        StringBuilder url = new($"/api/v1/jira/specifications/{Uri.EscapeDataString(spec)}");
        List<string> query = [];
        if (limit != null) query.Add($"limit={limit.Value}");
        if (offset != null) query.Add($"offset={offset.Value}");
        if (query.Count > 0) url.Append($"?{string.Join('&', query)}");

        JsonElement result = await client.GetFromOrchestratorAsync(url.ToString(), ct);
        return new { data = result };
    }
}
