using System.Net;
using System.Security.Cryptography;
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
    public async Task SupersedeUsesTypedOrchestratorRequest()
    {
        AuthoringHttpClient.EnsureOuterMode();
        DelegateHttpHandler handler = new(async (request, _, ct) =>
        {
            Assert.Equal(
                "/api/v1/processing-services/Planner/authoring/runs/run-1/items/item-1/supersede",
                request.RequestUri!.AbsolutePath);
            JsonElement body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(ct)).RootElement;
            Assert.Equal("obsolete", body.GetProperty("reason").GetString());
            return DelegateHttpHandler.Json(
                new AuthoringItemSupersedeResult(
                    "run-1",
                    "item-1",
                    "superseded",
                    "obsolete"));
        });

        object result = await PlannedTicketAuthoringHandler.HandleAsync(
            new PlannedTicketAuthoringRequest
            {
                Action = "supersede",
                RunId = "run-1",
                ItemId = "item-1",
                Reason = "obsolete",
            },
            "http://orchestrator",
            CancellationToken.None,
            handler);

        Assert.Equal(
            "superseded",
            Assert.IsType<AuthoringItemSupersedeResult>(result).Status);
    }

    [Fact]
    public async Task SupersedeRequiresReason()
    {
        DelegateHttpHandler handler = new((_, _, _) =>
            throw new InvalidOperationException("HTTP should not be called."));

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => PlannedTicketAuthoringHandler.HandleAsync(
                new PlannedTicketAuthoringRequest
                {
                    Action = "supersede",
                    RunId = "run-1",
                    ItemId = "item-1",
                    Reason = " ",
                },
                "http://orchestrator",
                CancellationToken.None,
                handler));

        Assert.Contains("reason", error.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SnapshotMaterializesTrustedPlannerPair()
    {
        AuthoringHttpClient.EnsureOuterMode();
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"fhir-augury-planner-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] bytes = [5, 4, 3, 2, 1];
            string snapshotPath = Path.Combine(
                directory,
                "planned-tickets.db");
            string descriptorPath = Path.Combine(
                directory,
                "planned-tickets.json");
            string hash = Convert.ToHexString(
                    SHA256.HashData(bytes))
                .ToLowerInvariant();
            DelegateHttpHandler handler = new((request, _, _) =>
                Task.FromResult(
                    request.RequestUri!.AbsolutePath.EndsWith(
                        "/bytes",
                        StringComparison.Ordinal)
                        ? new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(bytes),
                        }
                        : DelegateHttpHandler.Json(
                            new AuthoringSnapshotDescriptor(
                                "jira-fhir",
                                "run-1",
                                "snapshot-1",
                                1,
                                1,
                                1,
                                hash,
                                bytes.Length,
                                1,
                                1,
                                new Dictionary<string, long>(),
                                "planned-tickets.db",
                                DateTimeOffset.Parse(
                                    "2026-09-04T00:00:00Z")))));

            object result =
                await PlannedTicketAuthoringHandler.HandleAsync(
                    new PlannedTicketAuthoringRequest
                    {
                        Action = "snapshot",
                        RunId = "run-1",
                        SnapshotPath = snapshotPath,
                        DescriptorPath = descriptorPath,
                    },
                    "http://orchestrator",
                    CancellationToken.None,
                    handler);

            JsonElement json = JsonSerializer.SerializeToElement(result);
            Assert.Equal(
                snapshotPath,
                json.GetProperty("snapshotPath").GetString());
            Assert.Equal(bytes, await File.ReadAllBytesAsync(snapshotPath));
            Assert.Equal(2, handler.Calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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
