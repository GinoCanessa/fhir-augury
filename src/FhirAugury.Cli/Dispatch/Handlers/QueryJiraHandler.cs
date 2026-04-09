using System.Text.Json;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class QueryJiraHandler
{
    public static async Task<object> HandleAsync(QueryJiraRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);

        object queryBody = new
        {
            query = request.Query ?? "",
            statuses = request.Statuses,
            workGroups = request.WorkGroups,
            specifications = request.Specifications,
            types = request.Types,
            priorities = request.Priorities,
            labels = request.Labels,
            assignees = request.Assignees,
            reporters = request.Reporters,
            sortBy = request.SortBy,
            sortOrder = request.SortOrder,
            limit = request.Limit,
            offset = request.Offset,
            updatedAfter = request.UpdatedAfter,
            updatedBefore = request.UpdatedBefore,
            createdAfter = request.CreatedAfter,
            createdBefore = request.CreatedBefore,
        };

        JsonElement response = await client.QueryJiraViaOrchestratorAsync(queryBody, ct);

        List<object> results = [];
        if (response.TryGetProperty("results", out JsonElement resultsEl) && resultsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement issue in resultsEl.EnumerateArray())
            {
                results.Add(new
                {
                    key = issue.GetStringOrNull("key"),
                    projectKey = issue.GetStringOrNull("projectKey"),
                    title = issue.GetStringOrNull("title"),
                    type = issue.GetStringOrNull("type"),
                    status = issue.GetStringOrNull("status"),
                    priority = issue.GetStringOrNull("priority"),
                    workGroup = issue.GetStringOrNull("workGroup"),
                    specification = issue.GetStringOrNull("specification"),
                    updatedAt = issue.GetStringOrNull("updatedAt"),
                });
            }
        }

        return new { results };
    }
}
