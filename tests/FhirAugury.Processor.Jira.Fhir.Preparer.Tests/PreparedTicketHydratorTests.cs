using System.Net;
using System.Text;
using System.Text.Json;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketHydratorTests
{
    [Fact]
    public async Task Hydrate_HappyPath_WritesResolvedRowsForEverySource()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-100", jiraKey: "FHIR-200", zulipThread: "implementers:ballot", githubItem: "HL7/fhir#42", repo: "HL7/fhir");

        FakeHandler handler = new();
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-100",
            JsonMetadata(new Dictionary<string, string>
            {
                ["priority"] = "Major",
                ["resolution"] = "Persuasive",
                ["specification"] = "FHIR",
                ["comment_count"] = "5",
                ["description_plain"] = "body",
            }, title: "parent", url: "https://jira/browse/FHIR-100"));
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-200",
            JsonMetadata(new Dictionary<string, string>
            {
                ["status"] = "Open",
                ["type"] = "Change Request",
                ["priority"] = "Major",
                ["work_group"] = "FHIR-I",
            }, title: "related jira", url: "https://jira/browse/FHIR-200"));
        handler.AddJsonResponse("/api/v1/zulip/threads/implementers/ballot",
            """{"streamId":42,"stream":"implementers","topic":"ballot","url":"https://chat/x","messageCount":3,"firstMessageAt":"2026-05-01T00:00:00Z","lastMessageAt":"2026-05-02T00:00:00Z","firstMessageExcerpt":"hello"}""");
        handler.AddJsonResponse("/api/v1/github/items/HL7/fhir%2342",
            JsonMetadata(new Dictionary<string, string>
            {
                ["state"] = "open",
                ["is_pull_request"] = "False",
                ["repo"] = "HL7/fhir",
                ["number"] = "42",
                ["labels"] = "tracker-item",
            }, title: "github thing", url: "https://github.com/HL7/fhir/issues/42"));
        handler.AddJsonResponse("/api/v1/github/repos/HL7/fhir",
            """{"fullName":"HL7/fhir","description":"core","category":"FhirCore","url":"https://github.com/HL7/fhir"}""");

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-100", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-100");
        Assert.NotNull(read);
        Assert.Equal("resolved", read!.Parent!.HydrationStatus);
        Assert.Equal("FHIR", read.Parent.Specification);
        Assert.Equal(5, read.Parent.CommentCount);
        Assert.Equal("body", read.Parent.DescriptionPlain);
        Assert.Single(read.JiraRows);
        Assert.Equal("FHIR-200", read.JiraRows[0].JiraKey);
        Assert.Equal("resolved", read.JiraRows[0].HydrationStatus);
        Assert.Single(read.ZulipRows);
        Assert.Equal(42, read.ZulipRows[0].StreamId);
        Assert.Equal(3, read.ZulipRows[0].MessageCount);
        Assert.Single(read.GitHubRows);
        Assert.Equal("HL7", read.GitHubRows[0].Owner);
        Assert.Equal("fhir", read.GitHubRows[0].Repo);
        Assert.Equal(42, read.GitHubRows[0].Number);
        Assert.False(read.GitHubRows[0].IsPullRequest);
        Assert.Single(read.RepoRows);
        Assert.Equal("core", read.RepoRows[0].Description);
        Assert.Empty(read.JiraXrefRows);
    }

    [Fact]
    public async Task Hydrate_ParentJiraFetch404_WritesUnresolvedParentButContinuesChildren()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-101", jiraKey: "FHIR-300");

        FakeHandler handler = new();
        handler.AddStatusResponse("/api/v1/jira/items/FHIR-101", HttpStatusCode.NotFound);
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-300",
            JsonMetadata(new Dictionary<string, string> { ["status"] = "Open" }, title: "still here", url: "https://jira/browse/FHIR-300"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-101", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-101");
        Assert.NotNull(read);
        Assert.Equal("unresolved", read!.Parent!.HydrationStatus);
        Assert.Equal("orchestrator 404", read.Parent.HydrationReason);
        Assert.Single(read.JiraRows);
        Assert.Equal("resolved", read.JiraRows[0].HydrationStatus);
    }

    [Fact]
    public async Task Hydrate_RelatedJira503_WritesUnresolvedJiraRow_AndOtherSourcesUnaffected()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-102", jiraKey: "FHIR-400", repo: "HL7/fhir");

        FakeHandler handler = new();
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-102", JsonMetadata([], title: "p", url: "x"));
        handler.AddStatusResponse("/api/v1/jira/items/FHIR-400", HttpStatusCode.ServiceUnavailable);
        handler.AddJsonResponse("/api/v1/github/repos/HL7/fhir",
            """{"fullName":"HL7/fhir","description":"core","category":"FhirCore","url":"https://github.com/HL7/fhir"}""");

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-102", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-102");
        Assert.NotNull(read);
        Assert.Single(read!.JiraRows);
        Assert.Equal("unresolved", read.JiraRows[0].HydrationStatus);
        Assert.Equal("orchestrator 503", read.JiraRows[0].HydrationReason);
        Assert.Single(read.RepoRows);
        Assert.Equal("resolved", read.RepoRows[0].HydrationStatus);
    }

    [Fact]
    public async Task Hydrate_MalformedZulipThreadId_WritesUnresolvedThreadRow()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-103", zulipThread: "no-colon-here");

        FakeHandler handler = new();
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-103", JsonMetadata([], title: "p", url: "x"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-103", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-103");
        Assert.NotNull(read);
        Assert.Single(read!.ZulipRows);
        Assert.Equal("unresolved", read.ZulipRows[0].HydrationStatus);
        Assert.Equal("malformed thread id", read.ZulipRows[0].HydrationReason);
    }

    [Fact]
    public async Task Hydrate_GitHubFilePathKey_PopulatesPathAndNullsNumber()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-104", githubItem: "HL7/fhir:source/datatypes/dosage.html");

        FakeHandler handler = new();
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-104", JsonMetadata([], title: "p", url: "x"));
        handler.AddJsonResponse("/api/v1/github/items/HL7/fhir:source/datatypes/dosage.html",
            JsonMetadata(new Dictionary<string, string>
            {
                ["repo"] = "HL7/fhir",
                ["file_path"] = "source/datatypes/dosage.html",
            }, title: "dosage", url: "https://github.com/HL7/fhir/blob/main/source/datatypes/dosage.html"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-104", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-104");
        Assert.NotNull(read);
        PreparedGitHubHydrationRow row = Assert.Single(read!.GitHubRows);
        Assert.Equal("HL7", row.Owner);
        Assert.Equal("fhir", row.Repo);
        Assert.Equal("source/datatypes/dosage.html", row.Path);
        Assert.Null(row.Number);
        Assert.Equal("resolved", row.HydrationStatus);
    }

    [Fact]
    public async Task Hydrate_DuplicateOfDeRefDoesNotDoubleCountAgentPickedKey()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-105", jiraKey: "FHIR-500");

        FakeHandler handler = new();
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-105",
            JsonMetadata(new Dictionary<string, string>
            {
                ["duplicate_of"] = "FHIR-500",
                ["related_issues"] = "FHIR-501",
            }, title: "p", url: "x"));
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-500", JsonMetadata([], title: "dup", url: "x"));
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-501", JsonMetadata([], title: "rel", url: "x"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-105", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-105");
        Assert.NotNull(read);
        Assert.Equal(2, read!.JiraRows.Count);
        Assert.Contains(read.JiraRows, r => r.JiraKey == "FHIR-500");
        Assert.Contains(read.JiraRows, r => r.JiraKey == "FHIR-501");
        Assert.Equal(2, read.JiraXrefRows.Count);
        Assert.Contains(read.JiraXrefRows, x => x.Source == "DuplicateOf" && x.JiraKey == "FHIR-500");
        Assert.Contains(read.JiraXrefRows, x => x.Source == "RelatedIssues" && x.JiraKey == "FHIR-501");
    }

    [Fact]
    public async Task Hydrate_RelatedArtifactsNonJiraKey_IsDroppedFromXref()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-106");

        FakeHandler handler = new();
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-106",
            JsonMetadata(new Dictionary<string, string>
            {
                ["related_artifacts"] = "FHIR-9999, R4/observation",
            }, title: "p", url: "x"));
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-9999", JsonMetadata([], title: "art", url: "x"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-106", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-106");
        Assert.NotNull(read);
        PreparedTicketJiraXrefRow xref = Assert.Single(read!.JiraXrefRows);
        Assert.Equal("FHIR-9999", xref.JiraKey);
        Assert.Equal("RelatedArtifacts", xref.Source);
    }

    [Fact]
    public async Task Hydrate_OrchestratorTimeout_NeverThrows()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-107");

        FakeHandler handler = new();
        handler.AddExceptionResponse("/api/v1/jira/items/FHIR-107", new HttpRequestException("boom"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-107", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-107");
        Assert.NotNull(read);
        Assert.Equal("unresolved", read!.Parent!.HydrationStatus);
        Assert.StartsWith("orchestrator error:", read.Parent.HydrationReason);
    }

    [Fact]
    public async Task Hydrate_SecondCall_ReplacesPriorRows()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-108", jiraKey: "FHIR-600");

        FakeHandler firstHandler = new();
        firstHandler.AddJsonResponse("/api/v1/jira/items/FHIR-108", JsonMetadata([], title: "p1", url: "x"));
        firstHandler.AddJsonResponse("/api/v1/jira/items/FHIR-600", JsonMetadata([], title: "first", url: "x"));
        PreparedTicketHydrator firstHydrator = CreateHydrator(database, firstHandler);
        await firstHydrator.HydrateAsync("FHIR-108", CancellationToken.None);
        PreparedTicketHydrationReadModel? firstRead = await database.Database.GetHydrationAsync("FHIR-108");
        Assert.Equal("first", firstRead!.JiraRows[0].Title);

        FakeHandler secondHandler = new();
        secondHandler.AddJsonResponse("/api/v1/jira/items/FHIR-108", JsonMetadata([], title: "p2", url: "x"));
        secondHandler.AddJsonResponse("/api/v1/jira/items/FHIR-600", JsonMetadata([], title: "second", url: "x"));
        PreparedTicketHydrator secondHydrator = CreateHydrator(database, secondHandler);
        await secondHydrator.HydrateAsync("FHIR-108", CancellationToken.None);

        PreparedTicketHydrationReadModel? secondRead = await database.Database.GetHydrationAsync("FHIR-108");
        Assert.NotNull(secondRead);
        PreparedJiraHydrationRow row = Assert.Single(secondRead!.JiraRows);
        Assert.Equal("second", row.Title);
    }

    private static PreparedTicketHydrator CreateHydrator(TestDatabase database, FakeHandler handler)
    {
        HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        return new PreparedTicketHydrator(client, database.Database, NullLogger<PreparedTicketHydrator>.Instance);
    }

    private static async Task SeedAgentRowsAsync(TestDatabase database, string ticketKey, string? jiraKey = null, string? zulipThread = null, string? githubItem = null, string? repo = null)
    {
        PreparedTicketPayload payload = new()
        {
            Key = ticketKey,
            RequestSummary = "rs",
            CommentSummary = "cs",
            LinkedTicketSummary = "ls",
            RelatedTicketSummary = "rts",
            RelatedZulipSummary = "rzs",
            RelatedGitHubSummary = "rgs",
            ExistingProposed = "ep",
            ProposalA = "a",
            ProposalAJustification = "aj",
            ProposalAImpact = "Non-substantive",
            ProposalB = "b",
            ProposalBJustification = "bj",
            ProposalBImpact = "Compatible, substantive",
            ProposalC = "c",
            ProposalCJustification = "cj",
            Recommendation = "A",
            RecommendationJustification = "rj",
            SavedAt = DateTimeOffset.UtcNow,
            Repos = repo is null ? [] : [new PreparedTicketRepoPayload { Repo = repo, RepoCategory = "FhirCore", Justification = "r" }],
            RelatedJiraTickets = jiraKey is null ? [] : [new PreparedTicketRelatedJiraPayload { AssociatedTicketKey = jiraKey, LinkType = "related", Justification = "j" }],
            RelatedZulipThreads = zulipThread is null ? [] : [new PreparedTicketRelatedZulipPayload { ZulipThreadId = zulipThread, Justification = "z" }],
            RelatedGitHubItems = githubItem is null ? [] : [new PreparedTicketRelatedGitHubPayload { GitHubItemId = githubItem, Justification = "g" }],
        };
        await database.Database.SavePreparedTicketAsync(payload);
    }

    private static string JsonMetadata(Dictionary<string, string> metadata, string title, string url)
    {
        var payload = new
        {
            id = "x",
            title,
            url,
            metadata,
        };
        return JsonSerializer.Serialize(payload);
    }

    private static TestDatabase CreateDatabase()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "preparer-hydrator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preparer.db");
        PreparerDatabase database = new(path, NullLogger<PreparerDatabase>.Instance);
        database.Initialize();
        return new TestDatabase(directory, database);
    }

    private sealed class TestDatabase(string directory, PreparerDatabase database) : IDisposable
    {
        public PreparerDatabase Database { get; } = database;

        public void Dispose()
        {
            Database.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, ScriptedResponse> _byPath = new(StringComparer.Ordinal);

        public List<string> RequestedPaths { get; } = [];

        public void AddJsonResponse(string path, string json)
            => _byPath[path] = new ScriptedResponse(HttpStatusCode.OK, json, null);

        public void AddStatusResponse(string path, HttpStatusCode statusCode)
            => _byPath[path] = new ScriptedResponse(statusCode, null, null);

        public void AddExceptionResponse(string path, Exception exception)
            => _byPath[path] = new ScriptedResponse(HttpStatusCode.OK, null, exception);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string? query = request.RequestUri?.Query;
            if (!string.IsNullOrEmpty(query))
            {
                // strip query when matching by path
            }

            RequestedPaths.Add(path);

            if (!_byPath.TryGetValue(path, out ScriptedResponse? scripted))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"{{\"error\":\"unmocked path {path}\"}}", Encoding.UTF8, "application/json"),
                });
            }

            if (scripted.Exception is not null)
            {
                throw scripted.Exception;
            }

            HttpResponseMessage response = new(scripted.StatusCode);
            if (scripted.Json is not null)
            {
                response.Content = new StringContent(scripted.Json, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(response);
        }

        private sealed record ScriptedResponse(HttpStatusCode StatusCode, string? Json, Exception? Exception);
    }
}
