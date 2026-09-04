using System.Text.Json;
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketGroupingsControllerTests
{
    private const string WorkGroupClean = "OrdersAndObservations";
    private const string WorkGroupDisplay = "Orders and Observations";
    private const string Specification = "FHIR Core";
    private const string Type = "Change Request";

    [Fact]
    public async Task GetPartition_Returns404WhenEmpty()
    {
        using TestDatabase test = CreateDatabase();
        PreparedTicketGroupingsController controller = new(test.Database);

        ActionResult<PreparedTicketGroupingPartitionDto> result = await controller.GetPartition(WorkGroupClean, Specification, Type, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetPartition_RoundTripsPersistedGrouping()
    {
        using TestDatabase test = CreateDatabase();
        await SeedPreparedTicketAsync(test.Database, "FHIR-1");
        await SeedPreparedTicketAsync(test.Database, "FHIR-2");
        PreparedTicketGroupingPutRequest request = MinimalRequest();
        PreparedTicketGroupingSaveResult save =
            await SaveAsync(test.Database, request);
        Assert.Equal(1, save.TopicRows);
        Assert.Equal(1, save.TopicGroupRows);
        Assert.Equal(2, save.MemberRows);

        PreparedTicketGroupingsController controller = new(test.Database);
        ActionResult<PreparedTicketGroupingPartitionDto> getResult = await controller.GetPartition(WorkGroupClean, Specification, Type, CancellationToken.None);
        OkObjectResult getOk = Assert.IsType<OkObjectResult>(getResult.Result);
        PreparedTicketGroupingPartitionDto partition = Assert.IsType<PreparedTicketGroupingPartitionDto>(getOk.Value);
        Assert.Single(partition.Topics);
        PreparedTicketGroupingTopicDto topic = partition.Topics[0];
        Assert.Equal("Observation polymorphic value", topic.ShortDescription);
        Assert.Single(topic.LinkedTicketGroups);
        Assert.Equal("FHIR-1", topic.LinkedTicketGroups[0].FirstTicketKey);
        Assert.Equal(2, topic.LinkedTicketGroups[0].Members.Count);
    }

    [Fact]
    public async Task PersistenceRejectsUnknownTicketKey()
    {
        using TestDatabase test = CreateDatabase();
        await SeedPreparedTicketAsync(test.Database, "FHIR-1");
        PreparedTicketGroupingPutRequest request = new(
            WorkGroupDisplay,
            [
                new PreparedTicketGroupingTopicRequest(
                    "Short",
                    "Longer",
                    null,
                    [
                        new PreparedTicketGroupingLinkedGroupRequest(
                            "FHIR-1",
                            "rationale",
                            [
                                new PreparedTicketGroupingMemberRequest("FHIR-1", 0),
                                new PreparedTicketGroupingMemberRequest("FHIR-999", 1),
                            ]),
                    ],
                    []),
            ]);

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => SaveAsync(test.Database, request));
        Assert.Contains("FHIR-999", error.Message);
    }

    [Fact]
    public async Task PutPartition_ReplacesExistingPartition()
    {
        using TestDatabase test = CreateDatabase();
        await SeedPreparedTicketAsync(test.Database, "FHIR-1");
        await SeedPreparedTicketAsync(test.Database, "FHIR-2");
        await SeedPreparedTicketAsync(test.Database, "FHIR-3");
        await SeedPreparedTicketAsync(test.Database, "FHIR-4");
        await SaveAsync(test.Database, MinimalRequest());

        PreparedTicketGroupingPutRequest replacement = new(
            WorkGroupDisplay,
            [
                new PreparedTicketGroupingTopicRequest(
                    "Replaced",
                    "Longer replaced",
                    null,
                    [
                        new PreparedTicketGroupingLinkedGroupRequest(
                            "FHIR-3",
                            "second rationale",
                            [
                                new PreparedTicketGroupingMemberRequest("FHIR-3", 0),
                                new PreparedTicketGroupingMemberRequest("FHIR-4", 1),
                            ]),
                    ],
                    []),
            ]);

        await SaveAsync(test.Database, replacement);

        PreparedTicketGroupingsController controller = new(test.Database);
        ActionResult<PreparedTicketGroupingPartitionDto> getResult = await controller.GetPartition(WorkGroupClean, Specification, Type, CancellationToken.None);
        PreparedTicketGroupingPartitionDto partition = Assert.IsType<PreparedTicketGroupingPartitionDto>(Assert.IsType<OkObjectResult>(getResult.Result).Value);
        Assert.Single(partition.Topics);
        Assert.Equal("Replaced", partition.Topics[0].ShortDescription);
        Assert.Equal("FHIR-3", partition.Topics[0].LinkedTicketGroups[0].FirstTicketKey);
    }

    [Fact]
    public async Task PersistenceRejectsBlockMarkdownInRationale()
    {
        using TestDatabase test = CreateDatabase();
        await SeedPreparedTicketAsync(test.Database, "FHIR-1");
        await SeedPreparedTicketAsync(test.Database, "FHIR-2");
        PreparedTicketGroupingPutRequest request = new(
            WorkGroupDisplay,
            [
                new PreparedTicketGroupingTopicRequest(
                    "# Heading not allowed",
                    "Longer",
                    null,
                    [
                        new PreparedTicketGroupingLinkedGroupRequest(
                            "FHIR-1",
                            "rationale",
                            [
                                new PreparedTicketGroupingMemberRequest("FHIR-1", 0),
                                new PreparedTicketGroupingMemberRequest("FHIR-2", 1),
                            ]),
                    ],
                    []),
            ]);

        await Assert.ThrowsAsync<ArgumentException>(
            () => SaveAsync(test.Database, request));
    }

    [Fact]
    public async Task GetWorkGroup_ReturnsAllPartitionsAndIndividualTickets()
    {
        using TestDatabase test = CreateDatabase();
        await SeedPreparedTicketAsync(test.Database, "FHIR-1");
        await SeedPreparedTicketAsync(test.Database, "FHIR-2");
        await SeedPreparedTicketAsync(test.Database, "FHIR-77");
        await SeedHydrationSelfAsync(test.Database, "FHIR-77", WorkGroupDisplay, "Comment", Specification);

        PreparedTicketGroupingsController controller = new(test.Database);
        await SaveAsync(test.Database, MinimalRequest());

        ActionResult<PreparedTicketGroupingWorkGroupDto> result = await controller.GetWorkGroup(WorkGroupClean, CancellationToken.None);
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        PreparedTicketGroupingWorkGroupDto view = Assert.IsType<PreparedTicketGroupingWorkGroupDto>(ok.Value);
        Assert.Equal(2, view.Partitions.Count);
        Assert.Contains(view.Partitions, p => p.Type == Type);
        Assert.Contains(view.Partitions, p => p.Type == "Comment" && p.IndividualTicketKeys.Contains("FHIR-77"));
    }

    [Fact]
    public async Task PutPartition_RequiresAuthoringStage()
    {
        using TestDatabase test = CreateDatabase();
        PreparedTicketGroupingsController controller = new(test.Database);

        ActionResult<PreparedTicketGroupingSaveResultDto> result =
            await controller.PutPartition(
                WorkGroupClean,
                Specification,
                Type,
                MinimalRequest(),
                CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Json_RoundTrip_PreservesShape()
    {
        using TestDatabase test = CreateDatabase();
        await SeedPreparedTicketAsync(test.Database, "FHIR-1");
        await SeedPreparedTicketAsync(test.Database, "FHIR-2");
        PreparedTicketGroupingsController controller = new(test.Database);

        // Round-trip the request DTO through System.Text.Json before posting,
        // and the response DTO after it comes back, to guard against accidental
        // DTO renames.
        PreparedTicketGroupingPutRequest typed = MinimalRequest();
        string requestJson = JsonSerializer.Serialize(typed);
        PreparedTicketGroupingPutRequest? deserialized = JsonSerializer.Deserialize<PreparedTicketGroupingPutRequest>(requestJson);
        Assert.NotNull(deserialized);

        string encodedSpec = Uri.EscapeDataString(Specification);
        string encodedType = Uri.EscapeDataString(Type);
        Assert.NotEqual(Specification, encodedSpec);
        Assert.NotEqual(Type, encodedType);
        Assert.Equal(Specification, Uri.UnescapeDataString(encodedSpec));
        Assert.Equal(Type, Uri.UnescapeDataString(encodedType));

        await SaveAsync(test.Database, deserialized!);

        ActionResult<PreparedTicketGroupingPartitionDto> getResult = await controller.GetPartition(WorkGroupClean, Specification, Type, CancellationToken.None);
        PreparedTicketGroupingPartitionDto partition = Assert.IsType<PreparedTicketGroupingPartitionDto>(Assert.IsType<OkObjectResult>(getResult.Result).Value);
        string responseJson = JsonSerializer.Serialize(partition);
        Assert.Contains("WorkGroupClean", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Topics", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IndividualTicketKeys", responseJson, StringComparison.OrdinalIgnoreCase);
    }

    private static PreparedTicketGroupingPutRequest MinimalRequest() => new(
        WorkGroupDisplay,
        [
            new PreparedTicketGroupingTopicRequest(
                "Observation polymorphic value",
                "Covers ticket fan-out around Observation.value.",
                0,
                [
                    new PreparedTicketGroupingLinkedGroupRequest(
                        "FHIR-1",
                        "Both edit `Observation.value[x]`.",
                        [
                            new PreparedTicketGroupingMemberRequest("FHIR-1", 0),
                            new PreparedTicketGroupingMemberRequest("FHIR-2", 1),
                        ]),
                ],
                []),
        ]);

    private static Task<PreparedTicketGroupingSaveResult> SaveAsync(
        PreparerDatabase database,
        PreparedTicketGroupingPutRequest request) =>
        database.SaveGroupingAsync(
            PreparedTicketGroupingDtoMapper.ToPayload(
                WorkGroupClean,
                Specification,
                Type,
                request));

    private static async Task SeedPreparedTicketAsync(PreparerDatabase database, string key)
    {
        PreparedTicketPayload payload = new()
        {
            Key = key,
            RequestSummary = "summary",
            CommentSummary = "comments",
            LinkedTicketSummary = "linked",
            RelatedTicketSummary = "related",
            RelatedZulipSummary = "zulip",
            RelatedGitHubSummary = "github",
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
            Repos = [new PreparedTicketRepoPayload { Repo = "HL7/fhir", RepoCategory = "FHIR Core", Justification = "r" }],
        };
        await database.SavePreparedTicketAsync(payload);
    }

    private static async Task SeedHydrationSelfAsync(PreparerDatabase database, string ticketKey, string workGroup, string type, string specification)
    {
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        await using Microsoft.Data.Sqlite.SqliteConnection connection = database.OpenConnection();
        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_jira_hydration
            (Id, TicketKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain, WorkGroup, WorkGroupClean, Specification, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason)
            VALUES
            (@id, @ticket, @ticket, @title, @status, @type, @priority, NULL, NULL, @workGroup, @workGroupClean, @specification, @updatedAt, @url, @hydratedAt, 'resolved', NULL)
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@ticket", ticketKey);
        command.Parameters.AddWithValue("@title", "title");
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
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "preparer-groupings-controller-tests", Guid.NewGuid().ToString("N"));
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
            TestFileCleanup.SafeDeleteDirectory(directory);
        }
    }
}
