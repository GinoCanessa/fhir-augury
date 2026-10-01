using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Tests.Discovery;

public class JiraTicketDiscoveryClientTests
{
    private static readonly DateTimeOffset SourceRefreshAt =
        new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DirectClient_ListTickets_PostsLocalProcessingRequestWithShape()
    {
        CapturingHandler handler = new(
            CreatePage([CreateTicket("FHIR-1")], 0, 1));
        DirectJiraTicketDiscoveryClient client = new(CreateHttpClient(handler), Options(false), new JiraLocalProcessingRequestFactory());

        IReadOnlyList<JiraIssueSummaryEntry> tickets = await client.ListTicketsAsync(new ResolvedJiraProcessingFilters { SourceTicketShape = "fhir" }, CancellationToken.None);

        Assert.Single(tickets);
        Assert.Equal("api/v1/local-processing/tickets?type=fhir", handler.Requests[0].RequestUri!.PathAndQuery.TrimStart('/'));
        Assert.False(handler.RequestBodies[0]!.ProcessedLocally);
    }

    [Fact]
    public async Task DirectClient_RunBackedListOmitsProcessedLocallyFilter()
    {
        CapturingHandler handler = new(
            CreatePage([CreateTicket("FHIR-1")], 0, 1));
        DirectJiraTicketDiscoveryClient client = new(
            CreateHttpClient(handler),
            Options(false),
            new JiraLocalProcessingRequestFactory());

        IReadOnlyList<JiraIssueSummaryEntry> tickets = await client.ListTicketsForModeAsync(
            new ResolvedJiraProcessingFilters { SourceTicketShape = "fhir" },
            runBacked: true,
            CancellationToken.None);

        Assert.Single(tickets);
        Assert.Null(handler.RequestBodies[0]!.ProcessedLocally);
    }

    [Fact]
    public async Task OrchestratorClient_ListTickets_UsesJiraProxyRoute()
    {
        CapturingHandler handler = new(CreatePage([], 0, 0));
        OrchestratorJiraTicketDiscoveryClient client = new(CreateHttpClient(handler), Options(false), new JiraLocalProcessingRequestFactory());

        await client.ListTicketsAsync(new ResolvedJiraProcessingFilters { SourceTicketShape = "fhir" }, CancellationToken.None);

        Assert.Equal("api/v1/jira/local-processing/tickets?type=fhir", handler.Requests[0].RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [Fact]
    public async Task GetTicket_Fhir_MapsItemResponseToSummaryEntry()
    {
        ItemResponse item = new()
        {
            Source = "jira",
            Id = "FHIR-1",
            Title = "Title",
            Url = "https://jira/browse/FHIR-1",
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string> { ["status"] = "Triaged", ["type"] = "Change Request", ["work_group"] = "FHIR-I" },
        };
        DirectJiraTicketDiscoveryClient client = new(CreateHttpClient(new CapturingHandler(item)), Options(false), new JiraLocalProcessingRequestFactory());

        JiraIssueSummaryEntry? ticket = await client.GetTicketAsync("FHIR-1", "fhir", CancellationToken.None);

        Assert.NotNull(ticket);
        Assert.Equal("FHIR", ticket.ProjectKey);
        Assert.Equal("Triaged", ticket.Status);
        Assert.Equal("FHIR-I", ticket.WorkGroup);
    }

    [Fact]
    public async Task MarkProcessed_PostsSetProcessedOnlyWhenEnabled()
    {
        CapturingHandler disabledHandler = new(new JiraLocalProcessingSetResponse("FHIR-1", false, true));
        DirectJiraTicketDiscoveryClient disabledClient = new(CreateHttpClient(disabledHandler), Options(false), new JiraLocalProcessingRequestFactory());
        await disabledClient.MarkProcessedAsync("FHIR-1", "fhir", CancellationToken.None);
        Assert.Empty(disabledHandler.Requests);

        CapturingHandler enabledHandler = new(new JiraLocalProcessingSetResponse("FHIR-1", false, true));
        DirectJiraTicketDiscoveryClient enabledClient = new(CreateHttpClient(enabledHandler), Options(true), new JiraLocalProcessingRequestFactory());
        await enabledClient.MarkProcessedAsync("FHIR-1", "fhir", CancellationToken.None);
        Assert.Single(enabledHandler.Requests);
        Assert.Equal("api/v1/local-processing/set-processed?type=fhir", enabledHandler.Requests[0].RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [Fact]
    public async Task GetTicket_NonFhirShapeReturnsUnsupportedForV1()
    {
        DirectJiraTicketDiscoveryClient client = new(CreateHttpClient(new CapturingHandler(new object())), Options(false), new JiraLocalProcessingRequestFactory());

        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetTicketAsync("PSS-1", "pss", CancellationToken.None));
    }

    [Fact]
    public async Task SyncService_UpsertsAllReturnedTickets()
    {
        JiraIssueSummaryEntry[] page1 = CreateTickets(1, 500);
        JiraIssueSummaryEntry[] page2 = CreateTickets(501, 2);
        CapturingHandler handler = new(
        [
            CreatePage(page1, 0, 502),
            CreatePage(page2, 500, 502),
        ]);
        DirectJiraTicketDiscoveryClient client = new(CreateHttpClient(handler), Options(false), new JiraLocalProcessingRequestFactory());
        string path = Path.Combine(AppContext.BaseDirectory, $"jira-sync-{Guid.NewGuid():N}.db");
        JiraProcessingSourceTicketStore store = new(path);
        JiraProcessingDatabase processingDatabase = new(
            path,
            NullLogger<JiraProcessingDatabase>.Instance);
        processingDatabase.Initialize();
        AuthoringRunStore authoringStore = new(processingDatabase);
        JiraProcessingFilterResolver filterResolver = new();
        IOptions<JiraProcessingOptions> options = Options(false);
        JiraAuthoringRunCoordinator coordinator = new(
            authoringStore,
            store,
            new JiraConfiguredTicketSelector(store, new TestJiraTicketLabelMatcher()),
            filterResolver,
            options);
        JiraTicketSyncService service = new(
            client,
            store,
            authoringStore,
            coordinator,
            filterResolver,
            options,
            NullLogger<JiraTicketSyncService>.Instance);

        int count = await service.SyncAsync(CancellationToken.None);

        Assert.Equal(502, count);
        Assert.NotNull(await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None));
        Assert.NotNull(await store.GetByKeyAsync("FHIR-500", "fhir", CancellationToken.None));
        Assert.NotNull(await store.GetByKeyAsync("FHIR-501", "fhir", CancellationToken.None));
        JiraProcessingSourceTicketRecord? last =
            await store.GetByKeyAsync(
                "FHIR-502",
                "fhir",
                CancellationToken.None);
        Assert.NotNull(last);
        Assert.Equal(SourceRefreshAt, last.SourceProjectLastSuccessfulRefreshAt);
        Assert.Equal(7, last.SourceContentRevision);
    }

    [Fact]
    public async Task DirectClient_ListTickets_PaginatesUntilShortPage()
    {
        JiraIssueSummaryEntry[] page1 = CreateTickets(1, 500);
        JiraIssueSummaryEntry[] page2 = CreateTickets(501, 500);
        JiraIssueSummaryEntry[] page3 = CreateTickets(1001, 213);
        CapturingHandler handler = new(
        [
            CreatePage(page1, 0, 1213),
            CreatePage(page2, 500, 1213),
            CreatePage(page3, 1000, 1213),
        ]);
        DirectJiraTicketDiscoveryClient client = new(CreateHttpClient(handler), Options(false), new JiraLocalProcessingRequestFactory());

        IReadOnlyList<JiraIssueSummaryEntry> tickets = await client.ListTicketsAsync(new ResolvedJiraProcessingFilters { SourceTicketShape = "fhir" }, CancellationToken.None);

        Assert.Equal(1213, tickets.Count);
        for (int i = 0; i < tickets.Count; i++)
        {
            Assert.Equal($"FHIR-{i + 1}", tickets[i].Key);
        }
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(500, handler.RequestBodies[0]!.Limit);
        Assert.Equal(0, handler.RequestBodies[0]!.Offset);
        Assert.Equal(500, handler.RequestBodies[1]!.Limit);
        Assert.Equal(500, handler.RequestBodies[1]!.Offset);
        Assert.Equal(500, handler.RequestBodies[2]!.Limit);
        Assert.Equal(1000, handler.RequestBodies[2]!.Offset);
    }

    [Fact]
    public async Task DirectClient_ListTickets_ExactlyOneFullPage_IssuesTrailingEmptyPage()
    {
        JiraIssueSummaryEntry[] page1 = CreateTickets(1, 500);
        CapturingHandler handler = new(
        [
            CreatePage(page1, 0, 500),
            CreatePage([], 500, 500),
        ]);
        DirectJiraTicketDiscoveryClient client = new(CreateHttpClient(handler), Options(false), new JiraLocalProcessingRequestFactory());

        IReadOnlyList<JiraIssueSummaryEntry> tickets = await client.ListTicketsAsync(new ResolvedJiraProcessingFilters { SourceTicketShape = "fhir" }, CancellationToken.None);

        Assert.Equal(500, tickets.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(0, handler.RequestBodies[0]!.Offset);
        Assert.Equal(500, handler.RequestBodies[1]!.Offset);
    }

    [Fact]
    public async Task DirectClient_ListTickets_SinglePartialPage_IssuesOneRequest()
    {
        JiraIssueSummaryEntry[] page1 = CreateTickets(1, 7);
        CapturingHandler handler = new(
        [
            CreatePage(page1, 0, 7),
        ]);
        DirectJiraTicketDiscoveryClient client = new(CreateHttpClient(handler), Options(false), new JiraLocalProcessingRequestFactory());

        IReadOnlyList<JiraIssueSummaryEntry> tickets = await client.ListTicketsAsync(new ResolvedJiraProcessingFilters { SourceTicketShape = "fhir" }, CancellationToken.None);

        Assert.Equal(7, tickets.Count);
        Assert.Single(handler.Requests);
        Assert.Equal(500, handler.RequestBodies[0]!.Limit);
        Assert.Equal(0, handler.RequestBodies[0]!.Offset);
    }

    [Fact]
    public async Task OrchestratorClient_ListTickets_PaginatesUntilShortPage()
    {
        JiraIssueSummaryEntry[] page1 = CreateTickets(1, 500);
        JiraIssueSummaryEntry[] page2 = CreateTickets(501, 500);
        JiraIssueSummaryEntry[] page3 = CreateTickets(1001, 213);
        CapturingHandler handler = new(
        [
            CreatePage(page1, 0, 1213),
            CreatePage(page2, 500, 1213),
            CreatePage(page3, 1000, 1213),
        ]);
        OrchestratorJiraTicketDiscoveryClient client = new(CreateHttpClient(handler), Options(false), new JiraLocalProcessingRequestFactory());

        IReadOnlyList<JiraIssueSummaryEntry> tickets = await client.ListTicketsAsync(new ResolvedJiraProcessingFilters { SourceTicketShape = "fhir" }, CancellationToken.None);

        Assert.Equal(1213, tickets.Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(0, handler.RequestBodies[0]!.Offset);
        Assert.Equal(500, handler.RequestBodies[1]!.Offset);
        Assert.Equal(1000, handler.RequestBodies[2]!.Offset);
        Assert.All(handler.Requests, r => Assert.Equal("api/v1/jira/local-processing/tickets?type=fhir", r.RequestUri!.PathAndQuery.TrimStart('/')));
    }

    [Fact]
    public async Task ListTicketsWithProvenance_RestartsOnRevisionChange()
    {
        JiraIssueSummaryEntry[] firstPage = CreateTickets(1, 500);
        JiraIssueSummaryEntry[] changedPage = CreateTickets(501, 1);
        JiraIssueSummaryEntry[] retryFirstPage = CreateTickets(1001, 500);
        JiraIssueSummaryEntry[] retryLastPage = CreateTickets(1501, 1);
        CapturingHandler handler = new(
        [
            CreatePage(firstPage, 0, 501, revision: 7),
            CreatePage(changedPage, 500, 501, revision: 8),
            CreatePage(retryFirstPage, 0, 501, revision: 9),
            CreatePage(retryLastPage, 500, 501, revision: 9),
        ]);
        DirectJiraTicketDiscoveryClient client = new(
            CreateHttpClient(handler),
            Options(false),
            new JiraLocalProcessingRequestFactory());

        JiraTicketDiscoveryBatch batch =
            await client.ListTicketsWithProvenanceAsync(
                new ResolvedJiraProcessingFilters
                {
                    SourceTicketShape = "fhir",
                },
                CancellationToken.None);

        Assert.Equal(501, batch.Tickets.Count);
        Assert.Equal("FHIR-1001", batch.Tickets[0].Key);
        Assert.Equal("FHIR-1501", batch.Tickets[^1].Key);
        Assert.Equal(9, batch.Provenance!.ContentRevision);
        Assert.Equal(
            [0, 500, 0, 500],
            handler.RequestBodies.Select(request => request!.Offset).ToArray());
    }

    [Fact]
    public async Task ListTicketsWithProvenance_ThirdUnstablePassFinishesWithoutCoordinate()
    {
        JiraIssueSummaryEntry[] discarded = CreateTickets(1, 500);
        JiraIssueSummaryEntry[] retained = CreateTickets(1001, 500);
        JiraIssueSummaryEntry[] retainedTail = CreateTickets(1501, 1);
        CapturingHandler handler = new(
        [
            CreatePage(discarded, 0, 500, isStable: false),
            CreatePage(discarded, 0, 500, isStable: false),
            CreatePage(retained, 0, 501, isStable: false),
            CreatePage(retainedTail, 500, 501, isStable: false),
        ]);
        DirectJiraTicketDiscoveryClient client = new(
            CreateHttpClient(handler),
            Options(false),
            new JiraLocalProcessingRequestFactory());

        JiraTicketDiscoveryBatch batch =
            await client.ListTicketsWithProvenanceAsync(
                new ResolvedJiraProcessingFilters
                {
                    SourceTicketShape = "fhir",
                },
                CancellationToken.None);

        Assert.Equal(501, batch.Tickets.Count);
        Assert.Equal("FHIR-1001", batch.Tickets[0].Key);
        Assert.Equal("FHIR-1501", batch.Tickets[^1].Key);
        Assert.Null(batch.Provenance);
        Assert.Equal(
            [0, 0, 0, 500],
            handler.RequestBodies.Select(request => request!.Offset).ToArray());
    }

    [Fact]
    public async Task StableBatchProvenanceRetainsOnlyRepresentedProjects()
    {
        DateTimeOffset pssRefresh = SourceRefreshAt.AddHours(1);
        JiraIssueSummaryEntry pss = CreateTicket("PSS-1") with
        {
            ProjectKey = "PSS",
        };
        CapturingHandler handler = new(
            CreatePage(
                [CreateTicket("FHIR-1"), pss],
                0,
                2,
                watermarks: new Dictionary<string, DateTimeOffset?>
                {
                    ["FHIR"] = SourceRefreshAt,
                    ["PSS"] = pssRefresh,
                    ["OTHER"] = SourceRefreshAt.AddHours(2),
                }));
        OrchestratorJiraTicketDiscoveryClient client = new(
            CreateHttpClient(handler),
            Options(false),
            new JiraLocalProcessingRequestFactory());

        JiraTicketDiscoveryBatch batch =
            await client.ListTicketsWithProvenanceAsync(
                new ResolvedJiraProcessingFilters
                {
                    SourceTicketShape = "fhir",
                },
                CancellationToken.None);

        Assert.NotNull(batch.Provenance);
        Assert.Equal(2, batch.Provenance.ProjectLastSuccessfulRefreshAt.Count);
        Assert.Equal(
            SourceRefreshAt,
            batch.Provenance.ProjectLastSuccessfulRefreshAt["FHIR"]);
        Assert.Equal(
            pssRefresh,
            batch.Provenance.ProjectLastSuccessfulRefreshAt["PSS"]);
        Assert.DoesNotContain(
            "OTHER",
            batch.Provenance.ProjectLastSuccessfulRefreshAt.Keys);
        Assert.Equal(
            "api/v1/jira/local-processing/tickets?type=fhir",
            handler.Requests[0].RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [Fact]
    public async Task GetTicketWithProvenance_UsesOnlyStableMatchingProject()
    {
        ItemResponse item = new()
        {
            Source = "jira",
            Id = "FHIR-1",
            Title = "Title",
            Provenance = new SourceReadProvenance
            {
                Source = "jira",
                ContentRevision = 17,
                IsStable = true,
                ProjectLastSuccessfulRefreshAt =
                    new Dictionary<string, DateTimeOffset?>
                    {
                        ["FHIR"] = SourceRefreshAt,
                        ["PSS"] = SourceRefreshAt.AddHours(1),
                    },
            },
        };
        DirectJiraTicketDiscoveryClient client = new(
            CreateHttpClient(new CapturingHandler(item)),
            Options(false),
            new JiraLocalProcessingRequestFactory());

        JiraTicketDiscoveryItem discovery =
            (await client.GetTicketWithProvenanceAsync(
                "FHIR-1",
                "fhir",
                CancellationToken.None))!;

        Assert.Equal("FHIR-1", discovery.Ticket.Key);
        Assert.Equal(17, discovery.Provenance!.ContentRevision);
        KeyValuePair<string, DateTimeOffset?> project = Assert.Single(
            discovery.Provenance.ProjectLastSuccessfulRefreshAt);
        Assert.Equal("FHIR", project.Key);
        Assert.Equal(SourceRefreshAt, project.Value);

        DirectJiraTicketDiscoveryClient unstableClient = new(
            CreateHttpClient(new CapturingHandler(
                item with
                {
                    Provenance = item.Provenance with { IsStable = false },
                })),
            Options(false),
            new JiraLocalProcessingRequestFactory());
        JiraTicketDiscoveryItem unstable =
            (await unstableClient.GetTicketWithProvenanceAsync(
                "FHIR-1",
                "fhir",
                CancellationToken.None))!;
        Assert.Null(unstable.Provenance);
    }

    [Fact]
    public async Task LegacyDiscoveryImplementationReceivesNullProvenanceAdapter()
    {
        IJiraTicketDiscoveryClient client =
            new LegacyDiscoveryClient(CreateTicket("FHIR-1"));

        JiraTicketDiscoveryBatch batch =
            await client.ListTicketsWithProvenanceAsync(
                new ResolvedJiraProcessingFilters(),
                CancellationToken.None);
        JiraTicketDiscoveryItem item =
            (await client.GetTicketWithProvenanceAsync(
                "FHIR-1",
                "fhir",
                CancellationToken.None))!;

        Assert.Single(batch.Tickets);
        Assert.Null(batch.Provenance);
        Assert.Null(item.Provenance);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ActiveLabels_UseSelectionRouteAndConcreteJson(bool orchestrator, bool include, bool exclude)
    {
        ResolvedJiraProcessingFilters filters = ActiveFilters() with
        {
            LabelsToInclude = include ? [" inc_% ", "O'Reilly", @"back\slash", "MiXeD", "MiXeD"] : null,
            LabelsToExclude = exclude ? ["ex-01", "ex_%"] : null,
        };
        CapturingHandler handler = new(CreatePage([CreateTicket("FHIR-1")], 0, 1));
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketDiscoveryBatch batch = await client.ListTicketsWithProvenanceAsync(filters, CancellationToken.None);

        Assert.Equal("FHIR-1", Assert.Single(batch.Tickets).Key);
        Assert.Equal(7, batch.Provenance!.ContentRevision);
        Assert.Equal(SelectionPath(orchestrator), Assert.Single(handler.Requests).RequestUri!.PathAndQuery.TrimStart('/'));
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        using JsonDocument document = JsonDocument.Parse(Assert.Single(handler.RequestJson)!);
        JsonElement root = document.RootElement;
        Assert.Equal(filters.LabelsToInclude, ReadStrings(root.GetProperty("labelText").GetProperty("includes")));
        Assert.Equal(filters.LabelsToExclude, ReadStrings(root.GetProperty("labelText").GetProperty("excludes")));
        Assert.Equal(filters.TicketStatuses, ReadStrings(root.GetProperty("statuses")));
        Assert.Equal(filters.Projects, ReadStrings(root.GetProperty("projects")));
        Assert.Equal(filters.Specifications, ReadStrings(root.GetProperty("specifications")));
        Assert.Equal(filters.WorkGroups, ReadStrings(root.GetProperty("workGroups")));
        Assert.Equal(filters.TicketTypes, ReadStrings(root.GetProperty("types")));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("labels").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("keys").ValueKind);
        Assert.False(root.GetProperty("processedLocally").GetBoolean());
        Assert.Equal(500, root.GetProperty("limit").GetInt32());
        Assert.Equal(0, root.GetProperty("offset").GetInt32());
    }

    [Theory]
    [InlineData(false, "null")]
    [InlineData(false, "empty")]
    [InlineData(false, "blank")]
    [InlineData(true, "null")]
    [InlineData(true, "empty")]
    [InlineData(true, "blank")]
    public async Task InactiveLabels_UseOriginalRouteAndRequest(bool orchestrator, string kind)
    {
        IReadOnlyList<string>? labels = kind switch
        {
            "empty" => [],
            "blank" => [null!, "", "\t "],
            _ => null,
        };
        ResolvedJiraProcessingFilters filters = ActiveFilters() with
        {
            LabelsToInclude = labels,
            LabelsToExclude = labels,
        };
        CapturingHandler handler = new(CreatePage([], 0, 0));
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        Assert.Empty(await client.ListTicketsAsync(filters, CancellationToken.None));

        string path = orchestrator ? "api/v1/jira/local-processing/tickets?type=fhir" : "api/v1/local-processing/tickets?type=fhir";
        Assert.Equal(path, Assert.Single(handler.Requests).RequestUri!.PathAndQuery.TrimStart('/'));
        using JsonDocument document = JsonDocument.Parse(Assert.Single(handler.RequestJson)!);
        JsonElement root = document.RootElement;
        Assert.False(root.TryGetProperty("labelText", out _));
        Assert.False(root.TryGetProperty("keys", out _));
        Assert.Equal(filters.TicketStatuses, ReadStrings(root.GetProperty("statuses")));
        Assert.Equal(filters.Projects, ReadStrings(root.GetProperty("projects")));
        Assert.Equal(filters.Specifications, ReadStrings(root.GetProperty("specifications")));
        Assert.Equal(filters.WorkGroups, ReadStrings(root.GetProperty("workGroups")));
        Assert.Equal(filters.TicketTypes, ReadStrings(root.GetProperty("types")));
        Assert.False(root.GetProperty("processedLocally").GetBoolean());
        Assert.Equal(500, root.GetProperty("limit").GetInt32());
        Assert.Equal(0, root.GetProperty("offset").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InactiveLabels_PreserveLegacyResponseParsing(bool orchestrator)
    {
        CapturingHandler handler = new("{}");
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketDiscoveryBatch batch = await client.ListTicketsWithProvenanceAsync(
            new ResolvedJiraProcessingFilters(),
            CancellationToken.None);

        Assert.Empty(batch.Tickets);
        Assert.Null(batch.Provenance);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ActiveLabels_PreserveRunBackedProcessedState(bool orchestrator, bool runBacked)
    {
        CapturingHandler handler = new(CreatePage([], 0, 0));
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        await client.ListTicketsForModeAsync(ActiveFilters(), runBacked, CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(Assert.Single(handler.RequestJson)!);
        Assert.Equal(
            runBacked ? JsonValueKind.Null : JsonValueKind.False,
            document.RootElement.GetProperty("processedLocally").ValueKind);
        Assert.Equal(SelectionPath(orchestrator), handler.Requests[0].RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveLabels_GetAndMarkKeepOriginalRoutes(bool orchestrator)
    {
        CapturingHandler handler = new(
        [
            new ItemResponse { Source = "jira", Id = "FHIR-1", Title = "Title" },
            new JiraLocalProcessingSetResponse("FHIR-1", false, true),
        ]);
        IOptions<JiraProcessingOptions> options = Options(true);
        options.Value.LabelsToInclude = ["inc-01"];
        options.Value.LabelsToExclude = ["ex-01"];
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler, options);

        Assert.NotNull(await client.GetTicketAsync("FHIR-1", "fhir", CancellationToken.None));
        await client.MarkProcessedAsync("FHIR-1", "fhir", CancellationToken.None);

        string prefix = orchestrator ? "api/v1/jira" : "api/v1";
        Assert.Equal($"{prefix}/items/FHIR-1", handler.Requests[0].RequestUri!.PathAndQuery.TrimStart('/'));
        Assert.Equal($"{prefix}/local-processing/set-processed?type=fhir", handler.Requests[1].RequestUri!.PathAndQuery.TrimStart('/'));
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        using JsonDocument document = JsonDocument.Parse(handler.RequestJson[1]!);
        Assert.Equal("FHIR-1", document.RootElement.GetProperty("key").GetString());
        Assert.True(document.RootElement.GetProperty("processedLocally").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("labelText", out _));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveLabels_RestartPaginationOnRevisionChange(bool orchestrator)
    {
        CapturingHandler handler = new(
        [
            CreatePage(CreateTickets(1, 500), 0, 501, revision: 7),
            CreatePage(CreateTickets(501, 1), 500, 501, revision: 8),
            CreatePage(CreateTickets(1001, 500), 0, 501, revision: 9),
            CreatePage(CreateTickets(1501, 1), 500, 501, revision: 9),
        ]);
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketDiscoveryBatch batch = await client.ListTicketsWithProvenanceAsync(ActiveFilters(), CancellationToken.None);

        Assert.Equal(501, batch.Tickets.Count);
        Assert.Equal("FHIR-1001", batch.Tickets[0].Key);
        Assert.Equal("FHIR-1501", batch.Tickets[^1].Key);
        Assert.Equal(9, batch.Provenance!.ContentRevision);
        Assert.Equal(SourceRefreshAt, batch.Provenance.ProjectLastSuccessfulRefreshAt["FHIR"]);
        Assert.Equal([0, 500, 0, 500], handler.RequestBodies.Select(request => request!.Offset));
        Assert.All(handler.Requests, request => Assert.Equal(SelectionPath(orchestrator), request.RequestUri!.PathAndQuery.TrimStart('/')));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ActiveLabels_ThirdUnstablePassReturnsUnknownProvenance(bool orchestrator, bool missingProvenance)
    {
        JiraLocalProcessingListResponse[] pages =
        [
            CreatePage(CreateTickets(1, 500), 0, 500, isStable: false),
            CreatePage(CreateTickets(1, 500), 0, 500, isStable: false),
            CreatePage(CreateTickets(1001, 500), 0, 501, isStable: false),
            CreatePage(CreateTickets(1501, 1), 500, 501, isStable: false),
        ];
        CapturingHandler handler = new(pages.Select(page =>
            missingProvenance ? page with { Provenance = null } : page));
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketDiscoveryBatch batch = await client.ListTicketsWithProvenanceAsync(ActiveFilters(), CancellationToken.None);

        Assert.Equal(501, batch.Tickets.Count);
        Assert.Equal("FHIR-1001", batch.Tickets[0].Key);
        Assert.Equal("FHIR-1501", batch.Tickets[^1].Key);
        Assert.Null(batch.Provenance);
        Assert.Equal([0, 0, 0, 500], handler.RequestBodies.Select(request => request!.Offset));
        Assert.All(handler.Requests, request => Assert.Equal(SelectionPath(orchestrator), request.RequestUri!.PathAndQuery.TrimStart('/')));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 499)]
    [InlineData(true, 0)]
    [InlineData(true, 499)]
    public async Task ActiveLabels_ThirdUnstablePassAllowsShrinkingTotal(bool orchestrator, int shrunkenTotal)
    {
        CapturingHandler handler = new(
        [
            CreatePage(CreateTickets(1, 500), 0, 500, isStable: false),
            CreatePage(CreateTickets(1, 500), 0, 500, isStable: false),
            CreatePage(CreateTickets(1001, 500), 0, 750, isStable: false),
            CreatePage([], 500, shrunkenTotal, isStable: false),
        ]);
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketDiscoveryBatch batch = await client.ListTicketsWithProvenanceAsync(ActiveFilters(), CancellationToken.None);

        Assert.Equal(500, batch.Tickets.Count);
        Assert.Equal("FHIR-1001", batch.Tickets[0].Key);
        Assert.Equal("FHIR-1500", batch.Tickets[^1].Key);
        Assert.Null(batch.Provenance);
        Assert.Equal([0, 0, 0, 500], handler.RequestBodies.Select(request => request!.Offset));
        Assert.All(handler.Requests, request => Assert.Equal(SelectionPath(orchestrator), request.RequestUri!.PathAndQuery.TrimStart('/')));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveLabels_FullPageRetainsTrailingEmptyPage(bool orchestrator)
    {
        CapturingHandler handler = new(
        [
            CreatePage(CreateTickets(1, 500), 0, 500),
            CreatePage([], 500, 500),
        ]);
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketDiscoveryBatch batch = await client.ListTicketsWithProvenanceAsync(ActiveFilters(), CancellationToken.None);

        Assert.Equal(500, batch.Tickets.Count);
        Assert.Equal(7, batch.Provenance!.ContentRevision);
        Assert.Equal([0, 500], handler.RequestBodies.Select(request => request!.Offset));
    }

    [Theory]
    [InlineData(false, "results")]
    [InlineData(false, "limit")]
    [InlineData(false, "offset")]
    [InlineData(false, "total")]
    [InlineData(true, "results")]
    [InlineData(true, "limit")]
    [InlineData(true, "offset")]
    [InlineData(true, "total")]
    public async Task ActiveLabels_RejectMissingRequiredJsonMembers(bool orchestrator, string missingMember)
    {
        JsonObject envelope = JsonNode.Parse("""{"results":[],"limit":500,"offset":0,"total":0}""")!.AsObject();
        Assert.True(envelope.Remove(missingMember));
        CapturingHandler handler = new(envelope.ToJsonString());
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketSelectionUnavailableException exception = await Assert.ThrowsAsync<JiraTicketSelectionUnavailableException>(
            () => client.ListTicketsAsync(ActiveFilters(), CancellationToken.None));

        Assert.IsType<JsonException>(exception.InnerException);
        Assert.Contains("required response members", exception.Message);
        Assert.Single(handler.Requests);
    }

    public static IEnumerable<object[]> MalformedDiscoveryPages()
    {
        string[] cases =
        [
            "null", "invalid-json", "null-results", "non-array-results", "null-entry",
            "missing-key", "missing-title", "null-key", "empty-key", "blank-key",
            "duplicate-key", "case-duplicate-key", "negative-total", "wrong-offset",
            "negative-offset", "wrong-limit", "zero-limit", "negative-limit",
            "short-page", "early-empty-page", "oversized-page", "too-many-results",
        ];
        foreach (bool orchestrator in new[] { false, true })
        {
            foreach (string kind in cases)
            {
                yield return [orchestrator, kind];
            }
        }
    }

    [Theory]
    [MemberData(nameof(MalformedDiscoveryPages))]
    public async Task ActiveLabels_RejectIncompleteOrMalformedPayload(bool orchestrator, string kind)
    {
        object payload = kind switch
        {
            "null" => "null",
            "invalid-json" => "{not-json",
            "null-results" => """{"results":null,"limit":500,"offset":0,"total":0}""",
            "non-array-results" => """{"results":{},"limit":500,"offset":0,"total":0}""",
            "null-entry" => CreatePage([null!], 0, 1),
            "missing-key" => """{"results":[{"title":"Title"}],"limit":500,"offset":0,"total":1}""",
            "missing-title" => """{"results":[{"key":"FHIR-1"}],"limit":500,"offset":0,"total":1}""",
            "null-key" => CreatePage([CreateTicket(null!)], 0, 1),
            "empty-key" => CreatePage([CreateTicket("")], 0, 1),
            "blank-key" => CreatePage([CreateTicket(" \t")], 0, 1),
            "duplicate-key" => CreatePage([CreateTicket("FHIR-1"), CreateTicket("FHIR-1")], 0, 2),
            "case-duplicate-key" => CreatePage([CreateTicket("FHIR-1"), CreateTicket("fhir-1")], 0, 2),
            "negative-total" => CreatePage([], 0, -1),
            "wrong-offset" => CreatePage([], 1, 0),
            "negative-offset" => CreatePage([], -1, 0),
            "wrong-limit" => CreatePage([], 0, 0) with { Limit = 499 },
            "zero-limit" => CreatePage([], 0, 0) with { Limit = 0 },
            "negative-limit" => CreatePage([], 0, 0) with { Limit = -1 },
            "short-page" => CreatePage([CreateTicket("FHIR-1")], 0, 501),
            "early-empty-page" => CreatePage([], 0, 1),
            "oversized-page" => CreatePage(CreateTickets(1, 501), 0, 501),
            "too-many-results" => CreatePage(CreateTickets(1, 2), 0, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        CapturingHandler handler = new(payload);
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketSelectionUnavailableException exception = await Assert.ThrowsAsync<JiraTicketSelectionUnavailableException>(
            () => client.ListTicketsWithProvenanceAsync(ActiveFilters(), CancellationToken.None));

        Assert.NotNull(exception.InnerException);
        Assert.Single(handler.Requests);
        Assert.Equal(SelectionPath(orchestrator), handler.Requests[0].RequestUri!.PathAndQuery.TrimStart('/'));
    }

    [Theory]
    [InlineData(false, "title")]
    [InlineData(false, "projectKey")]
    [InlineData(false, "type")]
    [InlineData(false, "status")]
    [InlineData(false, "priority")]
    [InlineData(false, "workGroup")]
    [InlineData(false, "specification")]
    [InlineData(true, "title")]
    [InlineData(true, "projectKey")]
    [InlineData(true, "type")]
    [InlineData(true, "status")]
    [InlineData(true, "priority")]
    [InlineData(true, "workGroup")]
    [InlineData(true, "specification")]
    public async Task ActiveLabels_RejectNullSummaryText(bool orchestrator, string member)
    {
        JsonNode envelope = JsonSerializer.SerializeToNode(
            CreatePage([CreateTicket("FHIR-1")], 0, 1),
            JsonSerializerOptions.Web)!;
        envelope["results"]![0]![member] = null;
        CapturingHandler handler = new(envelope.ToJsonString());
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketSelectionUnavailableException exception = await Assert.ThrowsAsync<JiraTicketSelectionUnavailableException>(
            () => client.ListTicketsAsync(ActiveFilters(), CancellationToken.None));

        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ActiveLabels_LaterPageFailureDoesNotReturnPartialResults(bool orchestrator, bool malformed)
    {
        object failure = malformed
            ? """{"results":[],"limit":500,"offset":500}"""
            : new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("private upstream diagnostic"),
            };
        CapturingHandler handler = new(
        [
            CreatePage(CreateTickets(1, 500), 0, 501),
            failure,
        ]);
        JiraTicketDiscoveryClientBase client = CreateClient(orchestrator, handler);

        JiraTicketSelectionUnavailableException exception = await Assert.ThrowsAsync<JiraTicketSelectionUnavailableException>(
            () => client.ListTicketsWithProvenanceAsync(ActiveFilters(), CancellationToken.None));

        Assert.NotNull(exception.InnerException);
        Assert.DoesNotContain("private upstream diagnostic", exception.Message);
        Assert.Equal([0, 500], handler.RequestBodies.Select(request => request!.Offset));
        Assert.All(handler.Requests, request => Assert.Equal(SelectionPath(orchestrator), request.RequestUri!.PathAndQuery.TrimStart('/')));
    }

    private static ResolvedJiraProcessingFilters ActiveFilters() => new()
    {
        TicketStatuses = ["Triaged"],
        Projects = ["FHIR"],
        Specifications = ["fhir-core"],
        WorkGroups = ["FHIR-I"],
        TicketTypes = ["Change Request"],
        LabelsToInclude = ["inc-01"],
        LabelsToExclude = ["ex-01"],
    };

    private static JiraTicketDiscoveryClientBase CreateClient(
        bool orchestrator,
        HttpMessageHandler handler,
        IOptions<JiraProcessingOptions>? options = null)
        => orchestrator
            ? new OrchestratorJiraTicketDiscoveryClient(CreateHttpClient(handler), options ?? Options(false), new JiraLocalProcessingRequestFactory())
            : new DirectJiraTicketDiscoveryClient(CreateHttpClient(handler), options ?? Options(false), new JiraLocalProcessingRequestFactory());

    private static string SelectionPath(bool orchestrator) => orchestrator
        ? "api/v1/jira/local-processing/selection-tickets?type=fhir"
        : "api/v1/local-processing/selection-tickets?type=fhir";

    private static string[]? ReadStrings(JsonElement value) => value.ValueKind == JsonValueKind.Null
        ? null
        : value.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static JiraIssueSummaryEntry CreateTicket(string key) => new()
    {
        Key = key,
        ProjectKey = "FHIR",
        Title = "Title",
        Type = "Change Request",
        Status = "Triaged",
        WorkGroup = "FHIR-I",
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static JiraIssueSummaryEntry[] CreateTickets(int startKey, int count)
    {
        JiraIssueSummaryEntry[] tickets = new JiraIssueSummaryEntry[count];
        for (int i = 0; i < count; i++)
        {
            tickets[i] = CreateTicket($"FHIR-{startKey + i}");
        }
        return tickets;
    }

    private static JiraLocalProcessingListResponse CreatePage(
        IReadOnlyList<JiraIssueSummaryEntry> tickets,
        int offset,
        int total,
        long revision = 7,
        bool isStable = true,
        IReadOnlyDictionary<string, DateTimeOffset?>? watermarks = null)
        => new(tickets, 500, offset, total)
        {
            Provenance = new SourceReadProvenance
            {
                Source = "jira",
                ContentRevision = revision,
                IsStable = isStable,
                ProjectLastSuccessfulRefreshAt = watermarks ??
                    new Dictionary<string, DateTimeOffset?>
                    {
                        ["FHIR"] = SourceRefreshAt,
                    },
            },
        };

    private static IOptions<JiraProcessingOptions> Options(bool markProcessed) => Microsoft.Extensions.Options.Options.Create(new JiraProcessingOptions
    {
        AgentCliCommand = "agent {ticketKey}",
        JiraSourceAddress = "http://source",
        MarkUpstreamProcessedOnSuccess = markProcessed,
    });

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://localhost/") };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Queue<object> _scripted;
        private readonly object? _staticPayload;
        private readonly HttpStatusCode _statusCode;

        public CapturingHandler(object responsePayload, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _staticPayload = responsePayload;
            _scripted = new Queue<object>();
            _statusCode = statusCode;
        }

        public CapturingHandler(IEnumerable<object> scriptedPayloads, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _staticPayload = null;
            _scripted = new Queue<object>(scriptedPayloads);
            _statusCode = statusCode;
        }

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<JiraLocalProcessingListRequest?> RequestBodies { get; } = [];
        public List<string?> RequestJson { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            JiraLocalProcessingListRequest? body = null;
            string? raw = null;
            if (request.Content is not null)
            {
                raw = await request.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrEmpty(raw))
                {
                    try
                    {
                        body = System.Text.Json.JsonSerializer.Deserialize<JiraLocalProcessingListRequest>(
                            raw,
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch
                    {
                        body = null;
                    }
                }
            }
            RequestBodies.Add(body);
            RequestJson.Add(raw);

            object payload = _scripted.Count > 0 ? _scripted.Dequeue() : _staticPayload!;
            if (payload is HttpResponseMessage scriptedResponse)
            {
                return scriptedResponse;
            }
            HttpResponseMessage response = new(_statusCode)
            {
                Content = payload is string rawResponse
                    ? new StringContent(rawResponse, Encoding.UTF8, "application/json")
                    : JsonContent.Create(payload),
            };
            return response;
        }
    }

    private sealed class LegacyDiscoveryClient(JiraIssueSummaryEntry ticket)
        : IJiraTicketDiscoveryClient
    {
        public Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsAsync(
            ResolvedJiraProcessingFilters filters,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JiraIssueSummaryEntry>>([ticket]);

        public Task<JiraIssueSummaryEntry?> GetTicketAsync(
            string key,
            string sourceTicketShape,
            CancellationToken ct)
            => Task.FromResult<JiraIssueSummaryEntry?>(
                string.Equals(key, ticket.Key, StringComparison.OrdinalIgnoreCase)
                    ? ticket
                    : null);

        public Task MarkProcessedAsync(
            string key,
            string sourceTicketShape,
            CancellationToken ct)
            => Task.CompletedTask;
    }
}
