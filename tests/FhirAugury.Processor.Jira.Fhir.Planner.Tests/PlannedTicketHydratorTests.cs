using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Processor.Jira.Fhir.Planner.Hydration;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannedTicketHydratorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task HydrateAsync_Success_PersistsSelfRow()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPlanAsync(database.Database, "FHIR-201");
        FakeHandler handler = new(JsonMetadata("planned"));
        PlannedTicketHydrator hydrator = CreateHydrator(database.Database, handler);

        await hydrator.HydrateAsync("FHIR-201", CancellationToken.None);

        PlannedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-201");
        Assert.NotNull(read);
        Assert.Equal("resolved", read!.Parent!.HydrationStatus);
        Assert.Equal("planned", Assert.Single(read.JiraRows).Title);
        Assert.Equal("<p>body</p>", read.Parent.DescriptionHtml);
        Assert.Equal("<p>resolution</p>", read.Parent.ResolutionDescriptionHtml);
        Assert.Equal("Ada", read.Parent.Reporter);
        Assert.NotNull(read.Parent.CreatedAt);
        Assert.Equal("Patient, Observation", read.Parent.RelatedArtifactsRaw);
        Assert.Equal("patient.html", read.Parent.RelatedPagesRaw);
        using Microsoft.Data.Sqlite.SqliteConnection connection = database.Database.OpenConnection();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT DescriptionHtml, ResolutionDescriptionHtml FROM planned_ticket_jira_content WHERE TicketKey = 'FHIR-201'";
        using Microsoft.Data.Sqlite.SqliteDataReader content = command.ExecuteReader();
        Assert.True(content.Read());
        Assert.Equal("<p>body</p>", content.GetString(0));
        Assert.Equal("<p>resolution</p>", content.GetString(1));
    }

    [Fact]
    public async Task HydrateAsync_UnexpectedFailure_RemainsNonThrowing()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPlanAsync(database.Database, "FHIR-202");
        PlannedTicketHydrator hydrator = CreateHydrator(
            database.Database,
            new FakeHandler(new InvalidOperationException("script failed")));

        await hydrator.HydrateAsync("FHIR-202", CancellationToken.None);

        Assert.Null(await database.Database.GetHydrationAsync("FHIR-202"));
    }

    [Fact]
    public async Task HydrateAsync_CallerCancellation_Rethrows()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPlanAsync(database.Database, "FHIR-203");
        PlannedTicketHydrator hydrator = CreateHydrator(database.Database, new FakeHandler(JsonMetadata("planned")));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => hydrator.HydrateAsync("FHIR-203", cancellation.Token));
    }

    [Theory]
    [InlineData("321987")]
    [InlineData("000321987")]
    [InlineData("fhir/core:entry/request: réponse %2F 🩺")]
    public async Task HydrateAsync_ZulipResolver_PreservesAcceptedReferenceAndTypedContext(string reference)
    {
        using TestDatabase database = CreateDatabase();
        await SeedPlanAsync(database.Database, "FHIR-204", reference);
        ZulipReferenceResolutionResponse resolution = ZulipResolution(reference);
        FakeHandler handler = new(request => JsonResponse(
            request.RequestUri!.AbsolutePath == "/api/v1/zulip/references/resolve"
                ? JsonSerializer.Serialize(resolution, JsonOptions)
                : JsonMetadata("planned")));

        await CreateHydrator(database.Database, handler).HydrateAsync("FHIR-204", default);

        PlannedTicketHydrationReadModel read = Assert.IsType<PlannedTicketHydrationReadModel>(
            await database.Database.GetHydrationAsync("FHIR-204"));
        PlannedZulipHydrationRow row = Assert.Single(read.ZulipRows);
        Assert.Equal(reference, row.ZulipThreadId);
        Assert.Equal("resolved", row.HydrationStatus);
        Assert.Equal(resolution.StreamId, row.StreamId);
        Assert.Equal(resolution.StreamName, row.StreamName);
        Assert.Equal(resolution.Topic, row.Topic);
        Assert.Equal(resolution.Url, row.Url);
        Assert.Equal(resolution.FirstMessageAt, row.FirstMessageAt);
        Assert.Equal(resolution.LastMessageAt, row.LastMessageAt);
        Assert.True(ZulipReferenceHydrationReason.Read(row.HydrationReason).HasSourceBacking);
        Assert.Equal(ZulipReferenceLookupOutcome.Resolved, ReadZulipOutcome(row).LatestOutcome);
        Assert.Equal(
            $"/api/v1/zulip/references/resolve?reference={Uri.EscapeDataString(reference)}",
            Assert.Single(handler.Requests, path => path.StartsWith("/api/v1/zulip/", StringComparison.Ordinal)));
        Assert.Equal([reference], await database.Database.ListRelatedZulipThreadIdsForTicketAsync("FHIR-204", default));
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand command = new("SELECT ResolutionSummary FROM planned_tickets WHERE Key = 'FHIR-204'", connection);
        Assert.Equal("summary", command.ExecuteScalar());
    }

    [Theory]
    [InlineData("not-found", ZulipReferenceLookupOutcome.NotFound)]
    [InlineData("auth", ZulipReferenceLookupOutcome.AuthenticationFailed)]
    [InlineData("transient", ZulipReferenceLookupOutcome.TransientFailure)]
    [InlineData("empty", ZulipReferenceLookupOutcome.InvalidEnvelope)]
    [InlineData("mismatched", ZulipReferenceLookupOutcome.InvalidEnvelope)]
    [InlineData("invalid-json", ZulipReferenceLookupOutcome.InvalidJson)]
    public async Task HydrateAsync_ZulipFailure_PersistsSpecificUnresolvedRow(string fixture, ZulipReferenceLookupOutcome expected)
    {
        using TestDatabase database = CreateDatabase();
        await SeedPlanAsync(database.Database, "FHIR-205", "321987");
        FakeHandler handler = new(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/v1/zulip/references/resolve")
                return JsonResponse(JsonMetadata("planned"));
            return fixture switch
            {
                "not-found" => JsonResponse("{}", HttpStatusCode.NotFound),
                "auth" => JsonResponse("{}", HttpStatusCode.Unauthorized),
                "transient" => JsonResponse("{}", HttpStatusCode.ServiceUnavailable),
                "empty" => JsonResponse("{}"),
                "mismatched" => JsonResponse(JsonSerializer.Serialize(ZulipResolution("321988"), JsonOptions)),
                "invalid-json" => JsonResponse("{"),
                _ => throw new InvalidOperationException(fixture),
            };
        });

        await CreateHydrator(database.Database, handler).HydrateAsync("FHIR-205", default);

        PlannedTicketHydrationReadModel read = Assert.IsType<PlannedTicketHydrationReadModel>(
            await database.Database.GetHydrationAsync("FHIR-205"));
        PlannedZulipHydrationRow row = Assert.Single(read.ZulipRows);
        Assert.Equal("321987", row.ZulipThreadId);
        Assert.Equal("unresolved", row.HydrationStatus);
        Assert.Equal(expected, ReadZulipOutcome(row).LatestOutcome);
        Assert.False(ZulipReferenceHydrationReason.Read(row.HydrationReason).HasSourceBacking);
        Assert.Null(row.Url);
        Assert.Equal("resolved", read.Parent!.HydrationStatus);
        Assert.Equal(["321987"], await database.Database.ListRelatedZulipThreadIdsForTicketAsync("FHIR-205", default));
    }

    [Fact]
    public async Task HydrateAsync_ZulipWithoutOptionalDetailsStillPersistsBacking()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPlanAsync(database.Database, "FHIR-206", "321987");
        ZulipReferenceResolutionResponse resolution = ZulipResolution("321987") with
        {
            MessageCount = null,
            FirstMessageAt = null,
            LastMessageAt = null,
            FirstMessageExcerpt = null,
            Diagnostics = [ZulipReferenceDiagnosticCode.InvalidTimestamp],
        };
        FakeHandler handler = new(request => JsonResponse(
            request.RequestUri!.AbsolutePath == "/api/v1/zulip/references/resolve"
                ? JsonSerializer.Serialize(resolution, JsonOptions)
                : JsonMetadata("planned")));

        await CreateHydrator(database.Database, handler).HydrateAsync("FHIR-206", default);

        PlannedTicketHydrationReadModel read = Assert.IsType<PlannedTicketHydrationReadModel>(
            await database.Database.GetHydrationAsync("FHIR-206"));
        PlannedZulipHydrationRow row = Assert.Single(read.ZulipRows);
        Assert.Equal("resolved", row.HydrationStatus);
        Assert.Null(row.MessageCount);
        Assert.Null(row.FirstMessageAt);
        Assert.Null(row.LastMessageAt);
        Assert.True(ZulipReferenceHydrationReason.Read(row.HydrationReason).HasSourceBacking);
        Assert.Equal([ZulipReferenceDiagnosticCode.InvalidTimestamp], ReadZulipOutcome(row).Diagnostics);
    }

    private static PlannedTicketHydrator CreateHydrator(PlannerDatabase database, HttpMessageHandler handler)
    {
        HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        return new PlannedTicketHydrator(client, database, NullLogger<PlannedTicketHydrator>.Instance);
    }

    private static async Task SeedPlanAsync(PlannerDatabase database, string key, string? zulipReference = null)
    {
        await database.SavePlannedTicketAsync(new PlannedTicketPayload
        {
            Key = key,
            ResolutionSummary = "summary",
        });
        if (zulipReference is not null)
        {
            using SqliteConnection connection = database.OpenConnection();
            PlannedTicketRelatedZulipRecord.Insert(connection, new PlannedTicketRelatedZulipRecord
            {
                RowId = 1,
                IssueKey = key,
                ZulipThreadId = zulipReference,
            });
        }
    }

    private static ZulipReferenceResolutionResponse ZulipResolution(string reference)
    {
        const string stream = "fhir/core";
        const string topic = "entry/request: réponse %2F 🩺";
        bool isMessage = ZulipReferenceContract.TryGetMessageId(reference, out int messageId);
        string url = $"https://chat.example.com/#narrow/stream/{Uri.EscapeDataString(stream)}/topic/{Uri.EscapeDataString(topic)}";
        return new ZulipReferenceResolutionResponse
        {
            Reference = reference,
            Outcome = ZulipReferenceLookupOutcome.Resolved,
            Kind = isMessage ? ZulipReferenceKind.Message : ZulipReferenceKind.Thread,
            MessageId = isMessage ? messageId : null,
            StreamId = 9876,
            StreamName = stream,
            Topic = topic,
            Url = isMessage ? $"{url}/near/{messageId}" : url,
            MessageCount = 2,
            FirstMessageAt = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero),
            LastMessageAt = new DateTimeOffset(2026, 5, 2, 10, 0, 0, TimeSpan.Zero),
            FirstMessageExcerpt = "first body",
        };
    }

    private static ZulipReferenceHydrationOutcome ReadZulipOutcome(PlannedZulipHydrationRow row) =>
        Assert.IsType<ZulipReferenceHydrationOutcome>(ZulipReferenceHydrationReason.Read(row.HydrationReason).Metadata);

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        HttpResponseMessage response = new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }

    private static string JsonMetadata(string title) => JsonSerializer.Serialize(new
    {
        id = "x",
        title,
        url = "https://jira/browse/FHIR-201",
        content = "<p>body</p>",
        createdAt = "2026-04-01T00:00:00Z",
        metadata = new Dictionary<string, string>
        {
            ["status"] = "Triaged",
            ["type"] = "Change Request",
            ["work_group"] = "FHIR-I",
            ["specification"] = "FHIR",
            ["resolution_description"] = "<p>resolution</p>",
            ["reporter"] = "Ada",
            ["related_artifacts"] = "Patient, Observation",
            ["related_pages"] = "patient.html",
        },
    });

    private static TestDatabase CreateDatabase()
    {
        string directory = Path.Combine(
            Environment.CurrentDirectory,
            "temp",
            "planner-hydrator-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PlannerDatabase database = new(
            Path.Combine(directory, "planner.db"),
            NullLogger<PlannerDatabase>.Instance);
        database.Initialize();
        return new TestDatabase(directory, database);
    }

    private sealed class TestDatabase(string directory, PlannerDatabase database) : IDisposable
    {
        public PlannerDatabase Database { get; } = database;

        public void Dispose()
        {
            Database.Dispose();
            TestFileCleanup.SafeDeleteDirectory(directory);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string? _json;
        private readonly Exception? _exception;
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? _respond;
        public List<string> Requests { get; } = [];

        public FakeHandler(string json)
        {
            _json = json;
        }

        public FakeHandler(Exception exception)
        {
            _exception = exception;
        }

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!.PathAndQuery);
            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(_respond is not null ? _respond(request) : JsonResponse(_json!));
        }
    }
}
