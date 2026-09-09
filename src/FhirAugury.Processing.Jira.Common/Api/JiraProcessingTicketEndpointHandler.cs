using System.Text.RegularExpressions;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Authoring;
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
        AuthoringRunStore authoringStore,
        JiraAuthoringRunCoordinator runCoordinator,
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
        sourceTicketShape = "fhir";

        string mode = (await authoringStore.EnsureProcessorModeAsync(runCoordinator.ProcessorKind, ct: ct)).Mode;
        if (string.Equals(mode, AuthoringStatusValues.ProcessorModes.CuttingOver, StringComparison.Ordinal))
        {
            return Results.Json(
                new { error = "cutover-in-progress" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        JiraIssueSummaryEntry? ticket = await discoveryClient.GetTicketAsync(key, sourceTicketShape, ct);
        if (ticket is null)
        {
            return Results.NotFound(new { error = $"Ticket {key} was not found." });
        }

        bool runBacked = string.Equals(
            mode,
            AuthoringStatusValues.ProcessorModes.RunBacked,
            StringComparison.Ordinal);
        JiraProcessingSourceTicketRecord row = await store.UpsertAsync(
            ticket,
            sourceTicketShape,
            resetProcessingStatus: !runBacked,
            ct);
        if (runBacked)
        {
            JiraAuthoringRunCreation creation;
            try
            {
                creation = await runCoordinator.CreateOneItemRunAsync(
                    row,
                    databaseOnly: true,
                    ct);
            }
            catch (AuthoringConflictException ex)
                when (ex.Code == AuthoringConflictCode.ActiveRunCapacityReached)
            {
                return Results.Conflict(new
                {
                    error = "active-run-capacity-reached",
                    detail = ex.Message,
                });
            }
            string sourceRevision = JiraProcessingSourceTicketStore.GetSourceRevision(row);
            AuthoringRunItemRecord item = creation.Items.Single(value =>
                string.Equals(value.BusinessKey, row.Key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(value.ItemKind, sourceTicketShape, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(value.ExpectedSourceRevision, sourceRevision, StringComparison.Ordinal));
            return Results.Accepted(
                $"/processing/authoring/runs/{Uri.EscapeDataString(creation.Run.Id)}",
                new JiraProcessingEnqueueTicketResponse(
                    row.Id,
                    row.Key,
                    item.Status,
                    creation.Run.Id,
                    item.Id));
        }

        return Results.Accepted($"/processing/queue/{Uri.EscapeDataString(row.Id)}", new JiraProcessingEnqueueTicketResponse(row.Id, row.Key, row.ProcessingStatus));
    }

    [GeneratedRegex("^[A-Z][A-Z0-9]+-\\d+$")]
    private static partial Regex JiraKeyRegex();
}
