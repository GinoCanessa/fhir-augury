using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketClusteringSignalsControllerTests
{
    private const string WorkGroupClean = "OrdersandObservations";
    private const string WorkGroupDisplay = "Orders and Observations";
    private const string Specification = "FHIR Core";
    private const string Type = "Change Request";

    [Fact]
    public async Task GetWorkGroup_Returns404_WhenWorkgroupEmpty()
    {
        using TestDatabase test = CreateDatabase();
        PreparedTicketClusteringSignalsController controller = new(test.Database);

        ActionResult<PreparedTicketClusteringSignalsDto> result =
            await controller.GetWorkGroup(WorkGroupClean, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetWorkGroup_ReturnsTicketsWithSummariesAndLinks()
    {
        using TestDatabase test = CreateDatabase();
        await SeedHydrationSelfAsync(test.Database, "FHIR-1");
        await SeedHydrationSelfAsync(test.Database, "FHIR-2");
        await SeedPreparedTicketAsync(test.Database, "FHIR-1",
            relatedJira:
            [
                ("FHIR-2", "linked", "shared field"),
                ("FHIR-9", "related", "near-by"),
            ]);
        await SeedPreparedTicketAsync(test.Database, "FHIR-2");

        PreparedTicketClusteringSignalsController controller = new(test.Database);

        ActionResult<PreparedTicketClusteringSignalsDto> result =
            await controller.GetWorkGroup(WorkGroupClean, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PreparedTicketClusteringSignalsDto dto = Assert.IsType<PreparedTicketClusteringSignalsDto>(ok.Value);
        Assert.Equal(WorkGroupClean, dto.WorkGroupClean);
        Assert.Equal(WorkGroupDisplay, dto.WorkGroupDisplay);
        Assert.Equal(2, dto.Tickets.Count);

        PreparedTicketClusteringSignalDto first = dto.Tickets[0];
        Assert.Equal("FHIR-1", first.TicketKey);
        Assert.Equal(Specification, first.Specification);
        Assert.Equal(Type, first.Type);
        Assert.True(first.HasPreparedTicket);
        Assert.Equal(2, first.Links.Count);
        Assert.Contains(first.Links, l => l.AssociatedTicketKey == "FHIR-2" && l.LinkType == "linked");
        Assert.Contains(first.Links, l => l.AssociatedTicketKey == "FHIR-9" && l.LinkType == "related");

        PreparedTicketClusteringSignalDto second = dto.Tickets[1];
        Assert.Equal("FHIR-2", second.TicketKey);
        Assert.True(second.HasPreparedTicket);
        Assert.Empty(second.Links);
    }

    [Fact]
    public async Task GetWorkGroup_IncludesHydrationOnlyTicketWithEmptySummaries()
    {
        using TestDatabase test = CreateDatabase();
        await SeedHydrationSelfAsync(test.Database, "FHIR-1");

        PreparedTicketClusteringSignalsController controller = new(test.Database);

        ActionResult<PreparedTicketClusteringSignalsDto> result =
            await controller.GetWorkGroup(WorkGroupClean, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PreparedTicketClusteringSignalsDto dto = Assert.IsType<PreparedTicketClusteringSignalsDto>(ok.Value);
        PreparedTicketClusteringSignalDto only = Assert.Single(dto.Tickets);
        Assert.Equal("FHIR-1", only.TicketKey);
        Assert.False(only.HasPreparedTicket);
        Assert.Equal(string.Empty, only.RequestSummary);
        Assert.Empty(only.Links);
    }

    private static async Task SeedHydrationSelfAsync(PreparerDatabase database, string ticketKey)
    {
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        await using SqliteConnection connection = database.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_jira_hydration
            (Id, TicketKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain, WorkGroup, Specification, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason)
            VALUES
            (@id, @ticket, @ticket, @title, @status, @type, @priority, NULL, NULL, @workGroup, @specification, @updatedAt, @url, @hydratedAt, 'resolved', NULL)
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@ticket", ticketKey);
        command.Parameters.AddWithValue("@title", $"title-{ticketKey}");
        command.Parameters.AddWithValue("@status", "Open");
        command.Parameters.AddWithValue("@type", Type);
        command.Parameters.AddWithValue("@priority", "Major");
        command.Parameters.AddWithValue("@workGroup", WorkGroupDisplay);
        command.Parameters.AddWithValue("@specification", Specification);
        command.Parameters.AddWithValue("@updatedAt", hydratedAt.ToString("O"));
        command.Parameters.AddWithValue("@url", $"https://jira.example.com/{ticketKey}");
        command.Parameters.AddWithValue("@hydratedAt", hydratedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedPreparedTicketAsync(
        PreparerDatabase database,
        string key,
        IReadOnlyList<(string AssociatedKey, string LinkType, string Justification)>? relatedJira = null)
    {
        PreparedTicketPayload payload = new()
        {
            Key = key,
            RequestSummary = $"request-{key}",
            CommentSummary = $"comments-{key}",
            LinkedTicketSummary = $"linked-{key}",
            RelatedTicketSummary = $"related-{key}",
            RelatedZulipSummary = $"zulip-{key}",
            RelatedGitHubSummary = $"github-{key}",
            ExistingProposed = "existing",
            ProposalA = "A",
            ProposalAJustification = "a",
            ProposalAImpact = "Non-substantive",
            ProposalB = "B",
            ProposalBJustification = "b",
            ProposalBImpact = "Non-substantive",
            ProposalC = "C",
            ProposalCJustification = "c",
            Recommendation = "A",
            RecommendationJustification = "because",
            SavedAt = DateTimeOffset.Parse("2026-05-18T00:00:00Z"),
            RelatedJiraTickets = relatedJira?
                .Select(rj => new PreparedTicketRelatedJiraPayload
                {
                    AssociatedTicketKey = rj.AssociatedKey,
                    LinkType = rj.LinkType,
                    Justification = rj.Justification,
                })
                .ToList() ?? [],
        };
        await database.SavePreparedTicketAsync(payload);
    }

    private static TestDatabase CreateDatabase()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "preparer-clustering-signals-controller-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        PreparerDatabase database = new(Path.Combine(directory, "preparer.db"), NullLogger<PreparerDatabase>.Instance);
        database.Initialize();
        return new TestDatabase(directory, database);
    }

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
