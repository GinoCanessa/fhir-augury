using Fhiraugury;
using FhirAugury.Cli.Models;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class QueryJiraHandler
{
    public static async Task<object> HandleAsync(QueryJiraRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using GrpcClientFactory clients = new(orchestratorAddr);
        JiraQueryRequest grpcRequest = new()
        {
            Query = request.Query ?? "",
            SortBy = request.SortBy,
            SortOrder = request.SortOrder,
            Limit = request.Limit,
        };

        AddItems(grpcRequest.Statuses, request.Statuses);
        AddItems(grpcRequest.WorkGroups, request.WorkGroups);
        AddItems(grpcRequest.Specifications, request.Specifications);
        AddItems(grpcRequest.Types_, request.Types);
        AddItems(grpcRequest.Priorities, request.Priorities);
        AddItems(grpcRequest.Labels, request.Labels);
        AddItems(grpcRequest.Assignees, request.Assignees);

        if (!string.IsNullOrEmpty(request.UpdatedAfter) &&
            DateTimeOffset.TryParse(request.UpdatedAfter, out DateTimeOffset updatedAfter))
        {
            grpcRequest.UpdatedAfter = Timestamp.FromDateTimeOffset(updatedAfter);
        }

        using AsyncServerStreamingCall<JiraIssueSummary> call = clients.Jira.QueryIssues(grpcRequest, cancellationToken: ct);

        List<object> results = [];
        await foreach (JiraIssueSummary issue in call.ResponseStream.ReadAllAsync(ct))
        {
            results.Add(new
            {
                key = issue.Key,
                projectKey = issue.ProjectKey,
                title = issue.Title,
                type = issue.Type,
                status = issue.Status,
                priority = issue.Priority,
                workGroup = issue.WorkGroup,
                specification = issue.Specification,
                updatedAt = issue.UpdatedAt?.ToDateTimeOffset().ToString("o"),
            });
        }

        return new { results };
    }

    private static void AddItems(Google.Protobuf.Collections.RepeatedField<string> field, string[]? items)
    {
        if (items is null) return;
        foreach (string item in items)
            field.Add(item);
    }
}
