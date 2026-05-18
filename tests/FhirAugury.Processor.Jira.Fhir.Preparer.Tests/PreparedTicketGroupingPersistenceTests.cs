using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketGroupingPersistenceTests
{
    private const string WorkGroupClean = "OrdersandObservations";
    private const string WorkGroupDisplay = "Orders and Observations";
    private const string Specification = "FHIR Core";
    private const string Type = "Change Request";

    [Fact]
    public async Task SaveGrouping_InsertsTopicsGroupsAndMembers()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-50");

        PreparedTicketGroupingPayload payload = SamplePayload();

        PreparedTicketGroupingSaveResult result = await database.Database.SaveGroupingAsync(payload);

        Assert.Equal(1, Count(database, "prepared_ticket_topics"));
        Assert.Equal(1, Count(database, "prepared_ticket_topic_groups"));
        Assert.Equal(3, Count(database, "prepared_ticket_topic_members"));
        Assert.Equal(1, result.TopicRows);
        Assert.Equal(1, result.TopicGroupRows);
        Assert.Equal(3, result.MemberRows);
    }

    [Fact]
    public async Task SaveGrouping_ReplacesPartitionAtomically()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-50");
        await SeedPreparedTicketAsync(database, "FHIR-3");
        await SeedPreparedTicketAsync(database, "FHIR-4");
        await SeedPreparedTicketAsync(database, "FHIR-200");
        await SeedPreparedTicketAsync(database, "FHIR-201");

        // Neighbouring partition uses disjoint ticket keys so we can prove the
        // replacement only touches the target partition's rows.
        PreparedTicketGroupingPayload neighbour = SamplePayload();
        neighbour.Type = "Technical Correction";
        neighbour.Topics[0].LinkedTicketGroups[0].FirstTicketKey = "FHIR-200";
        neighbour.Topics[0].LinkedTicketGroups[0].Members =
        [
            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-200", Order = 0 },
            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-201", Order = 1 },
        ];
        neighbour.Topics[0].RemainingTicketKeys = [];
        await database.Database.SaveGroupingAsync(neighbour);

        await database.Database.SaveGroupingAsync(SamplePayload());

        PreparedTicketGroupingPayload replacement = SamplePayload();
        replacement.Topics[0].ShortDescription = "Replaced description";
        replacement.Topics[0].LinkedTicketGroups[0].Members =
        [
            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-3", Order = 0 },
            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-4", Order = 1 },
        ];
        replacement.Topics[0].LinkedTicketGroups[0].FirstTicketKey = "FHIR-3";
        replacement.Topics[0].RemainingTicketKeys = [];
        await database.Database.SaveGroupingAsync(replacement);

        Assert.Equal(2, Count(database, "prepared_ticket_topics"));
        Assert.Equal(2, Count(database, "prepared_ticket_topic_groups"));
        Assert.Equal(0, CountWhere(database, "prepared_ticket_topic_members", "TicketKey = 'FHIR-1'"));
        Assert.Equal(1, CountWhere(database, "prepared_ticket_topic_members", "TicketKey = 'FHIR-3'"));
        Assert.Equal(1, CountWhere(database, "prepared_ticket_topic_members", "TicketKey = 'FHIR-200'"));
        Assert.Equal(1, CountWhere(
            database,
            "prepared_ticket_topics",
            $"WorkGroupClean = '{WorkGroupClean}' AND Specification = '{Specification}' AND Type = 'Technical Correction'"));
    }

    [Fact]
    public async Task SaveGrouping_RejectsUnknownTicketKeys()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");

        PreparedTicketGroupingPayload payload = SamplePayload();

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() => database.Database.SaveGroupingAsync(payload));
        Assert.Contains("FHIR-2", ex.Message);

        Assert.Equal(0, Count(database, "prepared_ticket_topics"));
        Assert.Equal(0, Count(database, "prepared_ticket_topic_groups"));
        Assert.Equal(0, Count(database, "prepared_ticket_topic_members"));
    }

    [Fact]
    public async Task SaveGrouping_RejectsSingletonTopic()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");

        PreparedTicketGroupingPayload payload = SamplePayload();
        payload.Topics[0].LinkedTicketGroups.Clear();
        payload.Topics[0].RemainingTicketKeys = ["FHIR-1"];

        await Assert.ThrowsAsync<ArgumentException>(() => database.Database.SaveGroupingAsync(payload));

        Assert.Equal(0, Count(database, "prepared_ticket_topics"));
        Assert.Equal(0, Count(database, "prepared_ticket_topic_members"));
    }

    [Fact]
    public async Task SaveGrouping_RejectsDuplicateTicketAcrossTopicsInPartition()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-50");

        PreparedTicketGroupingPayload payload = SamplePayload();
        payload.Topics.Add(new PreparedTicketTopicPayload
        {
            ShortDescription = "Other topic",
            LongerDescription = "Other longer.",
            RemainingTicketKeys = ["FHIR-1", "FHIR-50"],
        });

        await Assert.ThrowsAsync<ArgumentException>(() => database.Database.SaveGroupingAsync(payload));

        Assert.Equal(0, Count(database, "prepared_ticket_topics"));
        Assert.Equal(0, Count(database, "prepared_ticket_topic_members"));
    }

    [Fact]
    public async Task GetGrouping_RendersHintedTopicsBeforeNullHintTopics()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-3");
        await SeedPreparedTicketAsync(database, "FHIR-4");
        await SeedPreparedTicketAsync(database, "FHIR-5");

        PreparedTicketGroupingPayload payload = SamplePayload();
        payload.Topics[0].RemainingTicketKeys = [];
        payload.Topics[0].RenderOrderHint = 5;
        payload.Topics[0].ShortDescription = "Hinted topic";
        payload.Topics[0].LinkedTicketGroups[0].Members =
        [
            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-1", Order = 0 },
            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-2", Order = 1 },
        ];
        payload.Topics[0].LinkedTicketGroups[0].FirstTicketKey = "FHIR-1";
        payload.Topics.Add(new PreparedTicketTopicPayload
        {
            ShortDescription = "Unhinted topic with more members",
            LongerDescription = "Has more total members but no hint.",
            RenderOrderHint = null,
            RemainingTicketKeys = ["FHIR-3", "FHIR-4", "FHIR-5"],
        });

        await database.Database.SaveGroupingAsync(payload);

        PreparedTicketGroupingPartition? partition = await database.Database.GetGroupingAsync(WorkGroupClean, Specification, Type);
        Assert.NotNull(partition);
        Assert.Equal(2, partition!.Topics.Count);
        Assert.Equal("Hinted topic", partition.Topics[0].ShortDescription);
        Assert.Equal("Unhinted topic with more members", partition.Topics[1].ShortDescription);
    }

    [Fact]
    public async Task GetGrouping_NullHintTopicsSortDescendingByMemberCountThenName()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-3");
        await SeedPreparedTicketAsync(database, "FHIR-4");
        await SeedPreparedTicketAsync(database, "FHIR-5");
        await SeedPreparedTicketAsync(database, "FHIR-6");
        await SeedPreparedTicketAsync(database, "FHIR-7");
        await SeedPreparedTicketAsync(database, "FHIR-8");
        await SeedPreparedTicketAsync(database, "FHIR-9");
        await SeedPreparedTicketAsync(database, "FHIR-10");
        await SeedPreparedTicketAsync(database, "FHIR-11");

        PreparedTicketGroupingPayload payload = new()
        {
            WorkGroupClean = WorkGroupClean,
            WorkGroupDisplay = WorkGroupDisplay,
            Specification = Specification,
            Type = Type,
            Topics =
            [
                new PreparedTicketTopicPayload
                {
                    ShortDescription = "Charlie",
                    LongerDescription = "third alphabetically",
                    RenderOrderHint = null,
                    RemainingTicketKeys = ["FHIR-1", "FHIR-2", "FHIR-3"],
                },
                new PreparedTicketTopicPayload
                {
                    ShortDescription = "Bravo",
                    LongerDescription = "second alphabetically",
                    RenderOrderHint = null,
                    RemainingTicketKeys = ["FHIR-4", "FHIR-5", "FHIR-6"],
                },
                new PreparedTicketTopicPayload
                {
                    ShortDescription = "Alpha big",
                    LongerDescription = "biggest by count",
                    RenderOrderHint = null,
                    RemainingTicketKeys = ["FHIR-7", "FHIR-8", "FHIR-9", "FHIR-10", "FHIR-11"],
                },
            ],
        };

        await database.Database.SaveGroupingAsync(payload);

        PreparedTicketGroupingPartition partition = (await database.Database.GetGroupingAsync(WorkGroupClean, Specification, Type))!;
        Assert.Equal(3, partition.Topics.Count);
        Assert.Equal("Alpha big", partition.Topics[0].ShortDescription);
        Assert.Equal("Bravo", partition.Topics[1].ShortDescription);
        Assert.Equal("Charlie", partition.Topics[2].ShortDescription);
    }

    [Fact]
    public async Task GetGrouping_ComputesIndividualTicketsViaPartitionPredicate()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-3");
        await SeedHydrationSelfAsync(database, "FHIR-1", WorkGroupDisplay, Type, Specification);
        await SeedHydrationSelfAsync(database, "FHIR-2", "Other Workgroup", Type, Specification);
        await SeedHydrationSelfAsync(database, "FHIR-3", WorkGroupDisplay, "Comment", Specification);

        PreparedTicketGroupingPayload payload = new()
        {
            WorkGroupClean = WorkGroupClean,
            WorkGroupDisplay = WorkGroupDisplay,
            Specification = Specification,
            Type = Type,
            Topics =
            [
                new PreparedTicketTopicPayload
                {
                    ShortDescription = "Has FHIR-1",
                    LongerDescription = "places FHIR-1 in a topic",
                    RemainingTicketKeys = ["FHIR-1", "FHIR-99"],
                },
            ],
        };
        await SeedPreparedTicketAsync(database, "FHIR-99");
        await database.Database.SaveGroupingAsync(payload);

        PreparedTicketGroupingPartition partition = (await database.Database.GetGroupingAsync(WorkGroupClean, Specification, Type))!;
        Assert.Empty(partition.IndividualTicketKeys);

        PreparedTicketGroupingPartition? commentPartition = await database.Database.GetGroupingAsync(WorkGroupClean, Specification, "Comment");
        Assert.NotNull(commentPartition);
        Assert.Single(commentPartition!.IndividualTicketKeys);
        Assert.Equal("FHIR-3", commentPartition.IndividualTicketKeys[0]);
    }

    [Fact]
    public async Task GetGrouping_ExcludesUnhydratedTicketsAndReportsCount()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedHydrationSelfAsync(database, "FHIR-1", WorkGroupDisplay, Type, Specification);
        // FHIR-2 has no self-hydration row.

        PreparedTicketGroupingPayload payload = new()
        {
            WorkGroupClean = WorkGroupClean,
            WorkGroupDisplay = WorkGroupDisplay,
            Specification = Specification,
            Type = Type,
            Topics =
            [
                new PreparedTicketTopicPayload
                {
                    ShortDescription = "topic",
                    LongerDescription = "longer",
                    RemainingTicketKeys = ["FHIR-1", "FHIR-2"],
                },
            ],
        };

        await database.Database.SaveGroupingAsync(payload);
        PreparedTicketGroupingPartition partition = (await database.Database.GetGroupingAsync(WorkGroupClean, Specification, Type))!;
        Assert.DoesNotContain("FHIR-2", partition.IndividualTicketKeys);
        Assert.Equal(1, partition.UnattributedTicketCount);
    }

    [Fact]
    public async Task SavePreparedTicket_DeletesGroupingMembersForKey()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-50");

        await database.Database.SaveGroupingAsync(SamplePayload());

        Assert.Equal(1, CountWhere(database, "prepared_ticket_topic_members", "TicketKey = 'FHIR-1'"));

        await SeedPreparedTicketAsync(database, "FHIR-1");

        Assert.Equal(0, CountWhere(database, "prepared_ticket_topic_members", "TicketKey = 'FHIR-1'"));
        Assert.Equal(1, Count(database, "prepared_ticket_topics"));
        Assert.Equal(1, Count(database, "prepared_ticket_topic_groups"));
    }

    [Fact]
    public async Task DeleteGroupingAsync_ClearsPartitionOnly()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-50");

        PreparedTicketGroupingPayload first = SamplePayload();
        PreparedTicketGroupingPayload second = SamplePayload();
        second.Type = "Technical Correction";

        await database.Database.SaveGroupingAsync(first);
        await database.Database.SaveGroupingAsync(second);

        await database.Database.DeleteGroupingAsync(WorkGroupClean, Specification, Type);

        Assert.Equal(1, Count(database, "prepared_ticket_topics"));
        Assert.Equal(1, Count(database, "prepared_ticket_topic_groups"));
        Assert.Equal(3, Count(database, "prepared_ticket_topic_members"));
        Assert.Equal(0, CountWhere(database, "prepared_ticket_topics", $"Type = '{Type}'"));
    }

    [Fact]
    public async Task GetWorkGroupGroupings_DiscoversPartitionsFromBothTablesAndResolvesDisplayName()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-50");
        await SeedPreparedTicketAsync(database, "FHIR-77");
        await SeedHydrationSelfAsync(database, "FHIR-77", WorkGroupDisplay, "Comment", Specification);

        await database.Database.SaveGroupingAsync(SamplePayload());

        PreparedTicketGroupingWorkGroupView? view = await database.Database.GetWorkGroupGroupingsAsync(WorkGroupClean);
        Assert.NotNull(view);
        Assert.Equal(WorkGroupDisplay, view!.WorkGroupDisplay);
        Assert.Equal(2, view.Partitions.Count);
        Assert.Contains(view.Partitions, p => p.Type == Type);
        Assert.Contains(view.Partitions, p => p.Type == "Comment");
    }

    private static PreparedTicketGroupingPayload SamplePayload() => new()
    {
        WorkGroupClean = WorkGroupClean,
        WorkGroupDisplay = WorkGroupDisplay,
        Specification = Specification,
        Type = Type,
        Topics =
        [
            new PreparedTicketTopicPayload
            {
                ShortDescription = "Observation value polymorphism",
                LongerDescription = "Covers ticket fan-out around Observation.value.",
                RenderOrderHint = 0,
                LinkedTicketGroups =
                [
                    new PreparedTicketTopicGroupPayload
                    {
                        FirstTicketKey = "FHIR-1",
                        Rationale = "Both edit `Observation.value[x]`.",
                        Members =
                        [
                            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-1", Order = 0 },
                            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-2", Order = 1 },
                        ],
                    },
                ],
                RemainingTicketKeys = ["FHIR-50"],
            },
        ],
    };

    private static async Task SeedPreparedTicketAsync(TestDatabase database, string key)
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
        await database.Database.SavePreparedTicketAsync(payload);
    }

    private static async Task SeedHydrationSelfAsync(TestDatabase database, string ticketKey, string workGroup, string type, string specification)
    {
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        await using SqliteConnection connection = database.Database.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_jira_hydration
            (Id, TicketKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain, WorkGroup, Specification, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason)
            VALUES
            (@id, @ticket, @ticket, @title, @status, @type, @priority, NULL, NULL, @workGroup, @specification, @updatedAt, @url, @hydratedAt, 'resolved', NULL)
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@ticket", ticketKey);
        command.Parameters.AddWithValue("@title", "title");
        command.Parameters.AddWithValue("@status", "Open");
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@priority", "Major");
        command.Parameters.AddWithValue("@workGroup", workGroup);
        command.Parameters.AddWithValue("@specification", specification);
        command.Parameters.AddWithValue("@updatedAt", hydratedAt.ToString("O"));
        command.Parameters.AddWithValue("@url", $"https://jira.example.com/{ticketKey}");
        command.Parameters.AddWithValue("@hydratedAt", hydratedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static int Count(TestDatabase database, string table)
    {
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int CountWhere(TestDatabase database, string table, string whereClause)
    {
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {whereClause}";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static TestDatabase CreateDatabase()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "preparer-grouping-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preparer.db");
        PreparerDatabase database = new(path, NullLogger<PreparerDatabase>.Instance);
        database.Initialize();
        return new TestDatabase(directory, database);
    }

    internal sealed class TestDatabase(string directory, PreparerDatabase database) : IDisposable
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
