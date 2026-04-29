using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketsControllerTests
{
    [Fact]
    public async Task GetPreparedTicket_Returns404ForUnknownKey()
    {
        using TestDatabase test = CreateDatabase();
        PreparedTicketsController controller = new(test.Database);

        ActionResult<PreparedTicketDetailDto> result = await controller.GetPreparedTicket("FHIR-404", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetPreparedTicket_ReturnsParentAndRelatedItems()
    {
        using TestDatabase test = CreateDatabase();
        await test.Database.SavePreparedTicketAsync(SamplePayload("FHIR-123"));
        PreparedTicketsController controller = new(test.Database);

        ActionResult<PreparedTicketDetailDto> result = await controller.GetPreparedTicket("FHIR-123", CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PreparedTicketDetailDto detail = Assert.IsType<PreparedTicketDetailDto>(ok.Value);
        Assert.Equal("FHIR-123", detail.Ticket.Key);
        Assert.Single(detail.RelatedItems.Repos);
    }

    [Fact]
    public async Task ListPreparedTickets_FiltersByRecommendationImpactAndRepo()
    {
        using TestDatabase test = CreateDatabase();
        PreparedTicketPayload first = SamplePayload("FHIR-123");
        first.Recommendation = "A";
        first.ProposalAImpact = "Non-substantive";
        PreparedTicketPayload second = SamplePayload("FHIR-124");
        second.Recommendation = "B";
        second.Repos[0].Repo = "Other/repo";
        await test.Database.SavePreparedTicketAsync(first);
        await test.Database.SavePreparedTicketAsync(second);
        PreparedTicketsController controller = new(test.Database);

        ActionResult<PreparedTicketListResponse> result = await controller.ListPreparedTickets("A", "Non-substantive", "HL7/fhir", null, 50, 0, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PreparedTicketListResponse response = Assert.IsType<PreparedTicketListResponse>(ok.Value);
        PreparedTicketSummaryDto row = Assert.Single(response.Items);
        Assert.Equal("FHIR-123", row.Key);
    }

    [Fact]
    public async Task QueryPreparedTickets_AppliesCombinedFilters()
    {
        using TestDatabase test = CreateDatabase();
        PreparedTicketPayload first = SamplePayload("FHIR-123");
        first.RelatedGitHubItems[0].GitHubItemId = "HL7/fhir#1";
        PreparedTicketPayload second = SamplePayload("FHIR-124");
        second.RelatedGitHubItems[0].GitHubItemId = "HL7/fhir#2";
        await test.Database.SavePreparedTicketAsync(first);
        await test.Database.SavePreparedTicketAsync(second);
        PreparedTicketsController controller = new(test.Database);
        PreparedTicketQueryRequest request = new() { GitHubItemId = "HL7/fhir#1", Recommendation = "A" };

        ActionResult<PreparedTicketListResponse> result = await controller.QueryPreparedTickets(request, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PreparedTicketListResponse response = Assert.IsType<PreparedTicketListResponse>(ok.Value);
        PreparedTicketSummaryDto row = Assert.Single(response.Items);
        Assert.Equal("FHIR-123", row.Key);
    }

    [Fact]
    public async Task GetPreparedTicketRelated_ReturnsOnlyRelatedItems()
    {
        using TestDatabase test = CreateDatabase();
        await test.Database.SavePreparedTicketAsync(SamplePayload("FHIR-123"));
        PreparedTicketsController controller = new(test.Database);

        ActionResult<PreparedTicketRelatedItemsDto> result = await controller.GetPreparedTicketRelated("FHIR-123", CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PreparedTicketRelatedItemsDto related = Assert.IsType<PreparedTicketRelatedItemsDto>(ok.Value);
        Assert.Single(related.Repos);
        Assert.Single(related.JiraTickets);
        Assert.Single(related.ZulipThreads);
        Assert.Single(related.GitHubItems);
    }

    private static TestDatabase CreateDatabase()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "preparer-controller-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PreparerDatabase database = new(Path.Combine(directory, "preparer.db"), NullLogger<PreparerDatabase>.Instance);
        database.Initialize();
        return new TestDatabase(directory, database);
    }

    private static PreparedTicketPayload SamplePayload(string key) => new()
    {
        Key = key,
        RequestSummary = "request",
        CommentSummary = "comments",
        LinkedTicketSummary = "linked",
        RelatedTicketSummary = "related",
        RelatedZulipSummary = "zulip",
        RelatedGitHubSummary = "github",
        ExistingProposed = "existing",
        ProposalA = "proposal a",
        ProposalAJustification = "why a",
        ProposalAImpact = "Non-substantive",
        ProposalB = "proposal b",
        ProposalBJustification = "why b",
        ProposalBImpact = "Compatible, substantive",
        ProposalC = "proposal c",
        ProposalCJustification = "why c",
        Recommendation = "A",
        RecommendationJustification = "because",
        Repos = [new PreparedTicketRepoPayload { Repo = "HL7/fhir", RepoCategory = "FHIR Core", Justification = "repo" }],
        RelatedJiraTickets = [new PreparedTicketRelatedJiraPayload { AssociatedTicketKey = "FHIR-999", LinkType = "related", Justification = "jira" }],
        RelatedZulipThreads = [new PreparedTicketRelatedZulipPayload { ZulipThreadId = "123", Justification = "zulip" }],
        RelatedGitHubItems = [new PreparedTicketRelatedGitHubPayload { GitHubItemId = "HL7/fhir#1", Justification = "github" }],
    };

    private sealed class TestDatabase(string directory, PreparerDatabase database) : IDisposable
    {
        public PreparerDatabase Database { get; } = database;

        public void Dispose()
        {
            Database.Dispose();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
