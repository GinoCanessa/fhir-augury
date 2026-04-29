using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace FhirAugury.Processing.Jira.Common.Api;

public static class JiraProcessingTicketEndpointExtensions
{
    public static IEndpointRouteBuilder MapJiraProcessingTicketEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapCore(endpoints, string.Empty);
        MapCore(endpoints, "/api/v1");
        return endpoints;
    }

    private static void MapCore(IEndpointRouteBuilder endpoints, string prefix)
    {
        endpoints.MapPost($"{prefix}/processing/tickets/{{key}}", JiraProcessingTicketEndpointHandler.EnqueueTicketAsync)
            .WithName(prefix.Length == 0 ? JiraProcessingEndpointRouteNames.EnqueueTicket : $"{JiraProcessingEndpointRouteNames.EnqueueTicket}ApiV1");
    }
}
