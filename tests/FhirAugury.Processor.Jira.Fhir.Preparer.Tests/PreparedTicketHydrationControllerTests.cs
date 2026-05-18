using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketHydrationControllerTests
{
    private const string WorkGroupClean = "OrdersAndObservations";
    private const string WorkGroupDisplay = "Orders and Observations";

    [Fact]
    public async Task GetWorkGroup_ReturnsEmptyItemsWhenNoHydration()
    {
        using TestDatabase test = CreateDatabase();
        PreparedTicketHydrationController controller = new(test.Database);

        ActionResult<PreparedJiraHydrationListResponse> result =
            await controller.GetWorkGroup(WorkGroupClean, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PreparedJiraHydrationListResponse response =
            Assert.IsType<PreparedJiraHydrationListResponse>(ok.Value);
        Assert.Equal(WorkGroupClean, response.WorkGroupClean);
        Assert.Null(response.WorkGroupDisplay);
        Assert.Empty(response.Items);
    }

    [Fact]
    public async Task GetWorkGroup_ReturnsHydratedTicketsForWorkGroup()
    {
        using TestDatabase test = CreateDatabase();
        await SeedHydrationSelfAsync(test.Database, "FHIR-2", WorkGroupDisplay, "Change Request", "FHIR Core");
        await SeedHydrationSelfAsync(test.Database, "FHIR-1", WorkGroupDisplay, "Comment", "FHIR Core");
        await SeedHydrationSelfAsync(test.Database, "FHIR-99", "Patient Care", "Change Request", "FHIR Core");
        PreparedTicketHydrationController controller = new(test.Database);

        ActionResult<PreparedJiraHydrationListResponse> result =
            await controller.GetWorkGroup(WorkGroupClean, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PreparedJiraHydrationListResponse response =
            Assert.IsType<PreparedJiraHydrationListResponse>(ok.Value);
        Assert.Equal(WorkGroupClean, response.WorkGroupClean);
        Assert.Equal(2, response.Items.Count);
        Assert.Equal("FHIR-1", response.Items[0].TicketKey);
        Assert.Equal("FHIR-2", response.Items[1].TicketKey);
        Assert.All(response.Items, item => Assert.Equal(WorkGroupDisplay, item.WorkGroup));
        Assert.DoesNotContain(response.Items, item => item.TicketKey == "FHIR-99");
    }

    [Fact]
    public async Task GetWorkGroup_ResolvesWorkGroupDisplayName()
    {
        using TestDatabase test = CreateDatabase();
        await SeedHydrationSelfAsync(test.Database, "FHIR-1", WorkGroupDisplay, "Change Request", "FHIR Core");
        PreparedTicketHydrationController controller = new(test.Database);

        ActionResult<PreparedJiraHydrationListResponse> result =
            await controller.GetWorkGroup(WorkGroupClean, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PreparedJiraHydrationListResponse response =
            Assert.IsType<PreparedJiraHydrationListResponse>(ok.Value);
        Assert.Equal(WorkGroupDisplay, response.WorkGroupDisplay);
    }

    [Fact]
    public async Task GetWorkGroup_ReturnsItemsEvenWhenDisplayNameIsUnresolved()
    {
        using TestDatabase test = CreateDatabase();
        // Seed a hydration row whose WorkGroup matches workGroupClean by the
        // REPLACE(' ', '') rule but is blank-after-trim, so the display
        // resolver returns null while the list query still finds it.
        await SeedHydrationSelfRawAsync(
            test.Database,
            ticketKey: "FHIR-1",
            workGroup: "   ",
            type: "Change Request",
            specification: "FHIR Core");
        PreparedTicketHydrationController controller = new(test.Database);

        ActionResult<PreparedJiraHydrationListResponse> result =
            await controller.GetWorkGroup(string.Empty, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PreparedJiraHydrationListResponse response =
            Assert.IsType<PreparedJiraHydrationListResponse>(ok.Value);
        Assert.Null(response.WorkGroupDisplay);
        Assert.Single(response.Items);
        Assert.Equal("FHIR-1", response.Items[0].TicketKey);
    }

    private static Task SeedHydrationSelfAsync(
        PreparerDatabase database,
        string ticketKey,
        string workGroup,
        string type,
        string specification)
        => SeedHydrationSelfRawAsync(database, ticketKey, workGroup, type, specification);

    private static async Task SeedHydrationSelfRawAsync(
        PreparerDatabase database,
        string ticketKey,
        string workGroup,
        string type,
        string specification)
    {
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        await using SqliteConnection connection = database.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_jira_hydration
            (Id, TicketKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain, WorkGroup, WorkGroupClean, Specification, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason)
            VALUES
            (@id, @ticket, @ticket, @title, @status, @type, @priority, NULL, NULL, @workGroup, @workGroupClean, @specification, @updatedAt, @url, @hydratedAt, 'resolved', NULL)
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@ticket", ticketKey);
        command.Parameters.AddWithValue("@title", $"Title for {ticketKey}");
        command.Parameters.AddWithValue("@status", "Open");
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@priority", "Major");
        command.Parameters.AddWithValue("@workGroup", workGroup);
        command.Parameters.AddWithValue("@workGroupClean", FhirAugury.Common.WorkGroups.Hl7WorkGroupNameCleaner.Clean(workGroup));
        command.Parameters.AddWithValue("@specification", specification);
        command.Parameters.AddWithValue("@updatedAt", hydratedAt.ToString("O"));
        command.Parameters.AddWithValue("@url", $"https://jira.example.com/{ticketKey}");
        command.Parameters.AddWithValue("@hydratedAt", hydratedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static TestDatabase CreateDatabase()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "preparer-hydration-controller-tests", Guid.NewGuid().ToString("N"));
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
