using System.Text.Json;
using FhirAugury.Cli.Dispatch.Handlers;
using FhirAugury.Cli.Models;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;

namespace FhirAugury.Cli.Tests.Authoring;

[Collection(AuthoringEnvironmentCollection.Name)]
public sealed class PlannedTicketAuthoringHandlerTests
{
    [Fact]
    public async Task WorkerSubmitRecoversFromResponseLossWithSameOperation()
    {
        using AuthoringEnvironmentScope environment = new();
        string? firstBody = null;
        DelegateHttpHandler handler = new(async (request, call, ct) =>
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            firstBody ??= body;
            Assert.Equal(firstBody, body);
            if (call == 1)
            {
                throw new HttpRequestException("response lost");
            }

            JsonElement submission = JsonDocument.Parse(body)
                .RootElement.GetProperty("submission");
            return DelegateHttpHandler.Json(new
            {
                receipt = new AuthoringResultReceipt(
                    "receipt-1",
                    "run-1",
                    "item-1",
                    "operation-1",
                    "FHIR-1",
                    submission.GetProperty("contentHash").GetString()!,
                    "revision-1",
                    "revision-1",
                    1,
                    DateTimeOffset.Parse("2026-09-04T00:00:00Z")),
                isReplay = true,
            });
        });

        object result = await PlannedTicketAuthoringHandler.HandleAsync(
            new PlannedTicketAuthoringRequest
            {
                Action = "submit",
                ObservedSourceRevision = "revision-1",
                Payload = new PlannedTicketPayload
                {
                    Key = "FHIR-1",
                    ResolutionSummary = "Implement the accepted change.",
                },
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        Assert.Equal(2, handler.Calls);
        Assert.True(Assert.IsType<AuthoringSubmitResponse>(result).IsReplay);
    }
}
