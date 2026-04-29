using System.Text.RegularExpressions;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Api;

public static partial class JiraProcessingTicketEndpointHandler
{
    public static async Task<IResult> EnqueueTicketAsync(
        string key,
        string? shape,
        IJiraTicketDiscoveryClient discoveryClient,
        JiraProcessingSourceTicketStore store,
        IOptions<JiraProcessingOptions> optionsAccessor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || !JiraKeyRegex().IsMatch(key))
        {
            return Results.BadRequest(new { error = "A valid Jira key is required." });
        }

        string sourceTicketShape = string.IsNullOrWhiteSpace(shape) ? optionsAccessor.Value.SourceTicketShape : shape;
        if (!string.Equals(sourceTicketShape, "fhir", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = $"Source ticket shape '{sourceTicketShape}' is not supported in v1." });
        }

        JiraIssueSummaryEntry? ticket = await discoveryClient.GetTicketAsync(key, sourceTicketShape, ct);
        if (ticket is null)
        {
            return Results.NotFound(new { error = $"Ticket {key} was not found." });
        }

        JiraProcessingSourceTicketRecord row = await store.UpsertAsync(ticket, sourceTicketShape, resetProcessingStatus: true, ct);
        return Results.Accepted($"/processing/queue/{Uri.EscapeDataString(row.Id)}", new JiraProcessingEnqueueTicketResponse(row.Id, row.Key, row.ProcessingStatus));
    }

    [GeneratedRegex("^[A-Z][A-Z0-9]+-\\d+$")]
    private static partial Regex JiraKeyRegex();
}
