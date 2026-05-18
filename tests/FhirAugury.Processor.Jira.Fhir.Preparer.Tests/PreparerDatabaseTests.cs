using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparerDatabaseTests
{
    [Fact]
    public void Initialize_CreatesPreparedTicketTablesAndIndexes()
    {
        using TestDatabase database = CreateDatabase();

        Assert.True(Exists(database, "table", "prepared_tickets"));
        Assert.True(Exists(database, "table", "prepared_ticket_repos"));
        Assert.True(Exists(database, "table", "prepared_ticket_related_jira"));
        Assert.True(Exists(database, "table", "prepared_ticket_related_zulip"));
        Assert.True(Exists(database, "table", "prepared_ticket_related_github"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_tickets"));
        Assert.True(HasUniqueIndexOver(database, "prepared_tickets", "Key"));
        Assert.True(HasUniqueIndexOver(database, "prepared_tickets", "Id"));
    }

    [Fact]
    public void Initialize_CreatesGroupingTablesAndIndexes()
    {
        using TestDatabase database = CreateDatabase();

        Assert.True(Exists(database, "table", "prepared_ticket_topics"));
        Assert.True(Exists(database, "table", "prepared_ticket_topic_groups"));
        Assert.True(Exists(database, "table", "prepared_ticket_topic_members"));

        Assert.True(IsRowIdPrimaryKey(database, "prepared_ticket_topics"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_ticket_topic_groups"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_ticket_topic_members"));

        Assert.True(HasUniqueIndexOver(database, "prepared_ticket_topics", "Id"));
        Assert.True(HasUniqueIndexOver(database, "prepared_ticket_topic_groups", "Id"));
        Assert.True(HasUniqueIndexOver(database, "prepared_ticket_topic_members", "Id"));

        Assert.True(HasUniqueIndexOverColumns(
            database,
            "prepared_ticket_topics",
            ["WorkGroupClean", "Specification", "Type", "ShortDescription"]));
        Assert.True(HasUniqueIndexOverColumns(
            database,
            "prepared_ticket_topic_groups",
            ["TopicRowId", "FirstTicketKey"]));
        Assert.True(HasUniqueIndexOverColumns(
            database,
            "prepared_ticket_topic_members",
            ["TopicRowId", "TicketKey"]));
    }

    [Fact]
    public void Initialize_CreatesHydrationTablesAndIndexes()
    {
        using TestDatabase database = CreateDatabase();

        Assert.True(Exists(database, "table", "prepared_ticket_hydration"));
        Assert.True(Exists(database, "table", "prepared_jira_hydration"));
        Assert.True(Exists(database, "table", "prepared_zulip_hydration"));
        Assert.True(Exists(database, "table", "prepared_github_hydration"));
        Assert.True(Exists(database, "table", "prepared_repo_hydration"));
        Assert.True(Exists(database, "table", "prepared_ticket_jira_xref"));

        Assert.True(IsRowIdPrimaryKey(database, "prepared_ticket_hydration"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_jira_hydration"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_zulip_hydration"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_github_hydration"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_repo_hydration"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_ticket_jira_xref"));

        Assert.True(HasUniqueIndexOver(database, "prepared_ticket_hydration", "TicketKey"));
        Assert.True(HasUniqueIndexOverColumns(database, "prepared_jira_hydration", ["TicketKey", "JiraKey"]));
        Assert.True(HasUniqueIndexOverColumns(database, "prepared_zulip_hydration", ["TicketKey", "ZulipThreadId"]));
        Assert.True(HasUniqueIndexOverColumns(database, "prepared_github_hydration", ["TicketKey", "GitHubItemId"]));
        Assert.True(HasUniqueIndexOverColumns(database, "prepared_repo_hydration", ["TicketKey", "Repo"]));
        Assert.True(HasUniqueIndexOverColumns(database, "prepared_ticket_jira_xref", ["TicketKey", "JiraKey", "Source"]));
    }

    [Fact]
    public async Task SavePreparedTicket_InsertsParentAndAllRelatedRows()
    {
        using TestDatabase database = CreateDatabase();
        PreparedTicketPayload payload = SamplePayload("FHIR-123");

        PreparedTicketSaveResult result = await database.Database.SavePreparedTicketAsync(payload);

        Assert.Equal("FHIR-123", result.Key);
        Assert.Equal(1, Count(database, "prepared_tickets"));
        Assert.Equal(1, Count(database, "prepared_ticket_repos"));
        Assert.Equal(1, Count(database, "prepared_ticket_related_jira"));
        Assert.Equal(1, Count(database, "prepared_ticket_related_zulip"));
        Assert.Equal(1, Count(database, "prepared_ticket_related_github"));
    }

    [Fact]
    public async Task SavePreparedTicket_OverwritesExistingParentAndChildrenAtomically()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SavePreparedTicketAsync(SamplePayload("FHIR-123"));
        PreparedTicketPayload replacement = SamplePayload("FHIR-123");
        replacement.Repos = [new PreparedTicketRepoPayload { Repo = "HL7/fhir-ig", RepoCategory = "IG", Justification = "new" }];
        replacement.RelatedJiraTickets = [];

        await database.Database.SavePreparedTicketAsync(replacement);

        Assert.Equal(1, Count(database, "prepared_tickets"));
        Assert.Equal(1, Count(database, "prepared_ticket_repos"));
        Assert.Equal(0, Count(database, "prepared_ticket_related_jira"));
        PreparedTicketDetail? detail = await database.Database.GetPreparedTicketAsync("FHIR-123");
        Assert.Equal("HL7/fhir-ig", detail!.RelatedItems.Repos[0].Repo);
    }

    [Fact]
    public async Task SavePreparedTicket_InvalidImpactDoesNotDeleteExistingRows()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SavePreparedTicketAsync(SamplePayload("FHIR-123"));
        PreparedTicketPayload invalid = SamplePayload("FHIR-123");
        invalid.ProposalAImpact = "bad";

        await Assert.ThrowsAsync<ArgumentException>(() => database.Database.SavePreparedTicketAsync(invalid));

        Assert.Equal(1, Count(database, "prepared_tickets"));
        Assert.Equal(1, Count(database, "prepared_ticket_repos"));
    }

    [Fact]
    public async Task SavePreparedTicket_InvalidRecommendationDoesNotDeleteExistingRows()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SavePreparedTicketAsync(SamplePayload("FHIR-123"));
        PreparedTicketPayload invalid = SamplePayload("FHIR-123");
        invalid.Recommendation = "Z";

        await Assert.ThrowsAsync<ArgumentException>(() => database.Database.SavePreparedTicketAsync(invalid));

        Assert.Equal(1, Count(database, "prepared_tickets"));
        Assert.Equal(1, Count(database, "prepared_ticket_repos"));
    }

    [Fact]
    public async Task ListPreparedTickets_FiltersByRecommendationAndImpact()
    {
        using TestDatabase database = CreateDatabase();
        PreparedTicketPayload first = SamplePayload("FHIR-123");
        first.Recommendation = "A";
        first.ProposalAImpact = "Non-substantive";
        PreparedTicketPayload second = SamplePayload("FHIR-124");
        second.Recommendation = "B";
        second.ProposalAImpact = "Compatible, substantive";
        await database.Database.SavePreparedTicketAsync(first);
        await database.Database.SavePreparedTicketAsync(second);

        IReadOnlyList<PreparedTicketSummary> rows = await database.Database.ListPreparedTicketsAsync(new PreparedTicketQueryFilter(Recommendation: "A", Impact: "Non-substantive"));

        PreparedTicketSummary row = Assert.Single(rows);
        Assert.Equal("FHIR-123", row.Key);
    }

    [Fact]
    public async Task GetPreparedTicket_ReturnsParentAndChildren()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SavePreparedTicketAsync(SamplePayload("FHIR-123"));

        PreparedTicketDetail? detail = await database.Database.GetPreparedTicketAsync("FHIR-123");

        Assert.NotNull(detail);
        Assert.Equal("FHIR-123", detail.Ticket.Key);
        Assert.Single(detail.RelatedItems.Repos);
        Assert.Single(detail.RelatedItems.JiraTickets);
        Assert.Single(detail.RelatedItems.ZulipThreads);
        Assert.Single(detail.RelatedItems.GitHubItems);
    }

    [Fact]
    public async Task SaveHydration_InsertsAllSixTables()
    {
        using TestDatabase database = CreateDatabase();
        PreparedTicketHydrationBatch batch = SampleBatch("FHIR-1");

        await database.Database.SaveHydrationAsync(batch);

        Assert.Equal(1, Count(database, "prepared_ticket_hydration"));
        Assert.Equal(1, Count(database, "prepared_jira_hydration"));
        Assert.Equal(1, Count(database, "prepared_zulip_hydration"));
        Assert.Equal(1, Count(database, "prepared_github_hydration"));
        Assert.Equal(1, Count(database, "prepared_repo_hydration"));
        Assert.Equal(1, Count(database, "prepared_ticket_jira_xref"));

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-1");
        Assert.NotNull(read);
        Assert.NotNull(read!.Parent);
        Assert.Equal("FHIR-1", read.Parent!.TicketKey);
        Assert.Equal("resolved", read.Parent.HydrationStatus);
        Assert.Single(read.JiraRows);
        Assert.Single(read.ZulipRows);
        Assert.Single(read.GitHubRows);
        Assert.Single(read.RepoRows);
        Assert.Single(read.JiraXrefRows);
    }

    [Fact]
    public async Task SaveHydration_ReplacesPriorRowsForSameTicket()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SaveHydrationAsync(SampleBatch("FHIR-1", jiraKey: "FHIR-100"));
        await database.Database.SaveHydrationAsync(SampleBatch("FHIR-1", jiraKey: "FHIR-200"));

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-1");

        Assert.NotNull(read);
        Assert.Single(read!.JiraRows);
        Assert.Equal("FHIR-200", read.JiraRows[0].JiraKey);
    }

    [Fact]
    public async Task SaveHydration_DoesNotTouchOtherTicketRows()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SaveHydrationAsync(SampleBatch("FHIR-1"));
        await database.Database.SaveHydrationAsync(SampleBatch("FHIR-2"));

        Assert.NotNull(await database.Database.GetHydrationAsync("FHIR-1"));
        Assert.NotNull(await database.Database.GetHydrationAsync("FHIR-2"));
        Assert.Equal(2, Count(database, "prepared_ticket_hydration"));
        Assert.Equal(2, Count(database, "prepared_jira_hydration"));
    }

    [Fact]
    public async Task SaveHydration_HonorsCompositeUniqueIndex()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SaveHydrationAsync(SampleBatch("FHIR-1"));
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        PreparedTicketHydrationBatch duplicate = new(
            TicketKey: "FHIR-9",
            Parent: SampleParent("FHIR-9", hydratedAt),
            JiraRows: [
                SampleJiraRow("FHIR-9", "FHIR-X", hydratedAt),
                SampleJiraRow("FHIR-9", "FHIR-X", hydratedAt),
            ],
            ZulipRows: [],
            GitHubRows: [],
            RepoRows: [],
            JiraXrefRows: []);

        await Assert.ThrowsAsync<SqliteException>(() => database.Database.SaveHydrationAsync(duplicate));

        Assert.Equal(1, Count(database, "prepared_ticket_hydration"));
        Assert.Equal(0, CountWhere(database, "prepared_ticket_hydration", "TicketKey = 'FHIR-9'"));
        Assert.Equal(0, CountWhere(database, "prepared_jira_hydration", "TicketKey = 'FHIR-9'"));
    }

    [Fact]
    public async Task SaveHydration_ParentUnresolvedDoesNotDropRelatedRows()
    {
        using TestDatabase database = CreateDatabase();
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        PreparedTicketHydrationBatch batch = new(
            TicketKey: "FHIR-1",
            Parent: new PreparedTicketHydrationRow(
                TicketKey: "FHIR-1",
                Priority: null,
                Resolution: null,
                ResolutionDescriptionPlain: null,
                Specification: null,
                RaisedInVersion: null,
                SelectedBallot: null,
                ChangeCategory: null,
                Impact: null,
                Labels: null,
                CommentCount: null,
                DescriptionPlain: null,
                HydratedAt: hydratedAt,
                HydrationStatus: "unresolved",
                HydrationReason: "orchestrator 503"),
            JiraRows: [SampleJiraRow("FHIR-1", "FHIR-100", hydratedAt)],
            ZulipRows: [SampleZulipRow("FHIR-1", "implementers:ballot", hydratedAt)],
            GitHubRows: [SampleGitHubRow("FHIR-1", "HL7/fhir#1", hydratedAt)],
            RepoRows: [SampleRepoRow("FHIR-1", "HL7/fhir", hydratedAt)],
            JiraXrefRows: []);

        await database.Database.SaveHydrationAsync(batch);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-1");
        Assert.NotNull(read);
        Assert.Equal("unresolved", read!.Parent!.HydrationStatus);
        Assert.Single(read.JiraRows);
        Assert.Single(read.ZulipRows);
        Assert.Single(read.GitHubRows);
        Assert.Single(read.RepoRows);
    }

    [Fact]
    public async Task ListJiraHydrationDisplayForWorkGroupAsync_ReturnsEmpty_WhenNoRows()
    {
        using TestDatabase database = CreateDatabase();

        IReadOnlyList<PreparedJiraHydrationRow> rows =
            await database.Database.ListJiraHydrationDisplayForWorkGroupAsync("OrdersandObservations");

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ListJiraHydrationDisplayForWorkGroupAsync_ReturnsOnlySelfRows()
    {
        using TestDatabase database = CreateDatabase();
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-1",
            jiraKey: "FHIR-1",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-1",
            jiraKey: "FHIR-555",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");

        IReadOnlyList<PreparedJiraHydrationRow> rows =
            await database.Database.ListJiraHydrationDisplayForWorkGroupAsync("OrdersandObservations");

        PreparedJiraHydrationRow only = Assert.Single(rows);
        Assert.Equal("FHIR-1", only.TicketKey);
        Assert.Equal("FHIR-1", only.JiraKey);
    }

    [Fact]
    public async Task ListJiraHydrationDisplayForWorkGroupAsync_MatchesWorkGroupClean()
    {
        using TestDatabase database = CreateDatabase();
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-1",
            jiraKey: "FHIR-1",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-2",
            jiraKey: "FHIR-2",
            workGroup: "Patient Care",
            type: "Change Request",
            specification: "FHIR Core");

        IReadOnlyList<PreparedJiraHydrationRow> rows =
            await database.Database.ListJiraHydrationDisplayForWorkGroupAsync("OrdersandObservations");

        PreparedJiraHydrationRow only = Assert.Single(rows);
        Assert.Equal("FHIR-1", only.TicketKey);
        Assert.Equal("Orders and Observations", only.WorkGroup);
    }

    [Fact]
    public async Task ListJiraHydrationDisplayForWorkGroupAsync_OrdersByTicketKey()
    {
        using TestDatabase database = CreateDatabase();
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-3",
            jiraKey: "FHIR-3",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-1",
            jiraKey: "FHIR-1",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-2",
            jiraKey: "FHIR-2",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");

        IReadOnlyList<PreparedJiraHydrationRow> rows =
            await database.Database.ListJiraHydrationDisplayForWorkGroupAsync("OrdersandObservations");

        Assert.Equal(["FHIR-1", "FHIR-2", "FHIR-3"], rows.Select(r => r.TicketKey).ToArray());
    }

    [Fact]
    public async Task ListJiraHydrationDisplayForWorkGroupAsync_IncludesNonOkStatus()
    {
        using TestDatabase database = CreateDatabase();
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-404",
            jiraKey: "FHIR-404",
            workGroup: "Orders and Observations",
            type: null,
            specification: null,
            title: null,
            status: null,
            hydrationStatus: "NotFound",
            hydrationReason: "jira returned 404");

        IReadOnlyList<PreparedJiraHydrationRow> rows =
            await database.Database.ListJiraHydrationDisplayForWorkGroupAsync("OrdersandObservations");

        PreparedJiraHydrationRow only = Assert.Single(rows);
        Assert.Equal("FHIR-404", only.TicketKey);
        Assert.Equal("NotFound", only.HydrationStatus);
        Assert.Equal("jira returned 404", only.HydrationReason);
        Assert.Null(only.Title);
        Assert.Null(only.Status);
        Assert.Null(only.Type);
        Assert.Null(only.Specification);
    }

    private static async Task SeedHydrationRowAsync(
        PreparerDatabase database,
        string ticketKey,
        string jiraKey,
        string workGroup,
        string? type,
        string? specification,
        string? title = "title",
        string? status = "Open",
        string hydrationStatus = "resolved",
        string? hydrationReason = null)
    {
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        await using SqliteConnection connection = database.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_jira_hydration
            (Id, TicketKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain, WorkGroup, Specification, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason)
            VALUES
            (@id, @ticket, @jira, @title, @status, @type, NULL, NULL, NULL, @workGroup, @specification, @updatedAt, @url, @hydratedAt, @hydrationStatus, @hydrationReason)
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@ticket", ticketKey);
        command.Parameters.AddWithValue("@jira", jiraKey);
        command.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("@type", (object?)type ?? DBNull.Value);
        command.Parameters.AddWithValue("@workGroup", workGroup);
        command.Parameters.AddWithValue("@specification", (object?)specification ?? DBNull.Value);
        command.Parameters.AddWithValue("@updatedAt", hydratedAt.ToString("O"));
        command.Parameters.AddWithValue("@url", $"https://jira.example.com/{jiraKey}");
        command.Parameters.AddWithValue("@hydratedAt", hydratedAt.ToString("O"));
        command.Parameters.AddWithValue("@hydrationStatus", hydrationStatus);
        command.Parameters.AddWithValue("@hydrationReason", (object?)hydrationReason ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static PreparedTicketHydrationBatch SampleBatch(string ticketKey, string jiraKey = "FHIR-100")
    {
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        return new PreparedTicketHydrationBatch(
            TicketKey: ticketKey,
            Parent: SampleParent(ticketKey, hydratedAt),
            JiraRows: [SampleJiraRow(ticketKey, jiraKey, hydratedAt)],
            ZulipRows: [SampleZulipRow(ticketKey, "implementers:ballot", hydratedAt)],
            GitHubRows: [SampleGitHubRow(ticketKey, "HL7/fhir#1", hydratedAt)],
            RepoRows: [SampleRepoRow(ticketKey, "HL7/fhir", hydratedAt)],
            JiraXrefRows: [new PreparedTicketJiraXrefRow(ticketKey, "FHIR-9999", "RelatedIssues")]);
    }

    private static PreparedTicketHydrationRow SampleParent(string ticketKey, DateTimeOffset hydratedAt) =>
        new(
            TicketKey: ticketKey,
            Priority: "Major",
            Resolution: "Persuasive",
            ResolutionDescriptionPlain: "done",
            Specification: "FHIR",
            RaisedInVersion: "5.0.0",
            SelectedBallot: "2026-Jan",
            ChangeCategory: "Refinement",
            Impact: "Compatible, substantive",
            Labels: null,
            CommentCount: 3,
            DescriptionPlain: "body text",
            HydratedAt: hydratedAt,
            HydrationStatus: "resolved",
            HydrationReason: null);

    private static PreparedJiraHydrationRow SampleJiraRow(string ticketKey, string jiraKey, DateTimeOffset hydratedAt) =>
        new(
            TicketKey: ticketKey,
            JiraKey: jiraKey,
            Title: "title",
            Status: "Open",
            Type: "Change Request",
            Priority: "Major",
            Resolution: null,
            ResolutionDescriptionPlain: null,
            WorkGroup: "FHIR-I",
            Specification: "FHIR",
            UpdatedAt: hydratedAt,
            Url: $"https://jira.example.com/browse/{jiraKey}",
            HydratedAt: hydratedAt,
            HydrationStatus: "resolved",
            HydrationReason: null);

    private static PreparedZulipHydrationRow SampleZulipRow(string ticketKey, string threadId, DateTimeOffset hydratedAt) =>
        new(
            TicketKey: ticketKey,
            ZulipThreadId: threadId,
            StreamId: 42,
            StreamName: "implementers",
            Topic: "ballot",
            MessageCount: 3,
            FirstMessageAt: hydratedAt,
            LastMessageAt: hydratedAt,
            FirstMessageExcerpt: "first",
            Url: "https://chat.example.com/",
            HydratedAt: hydratedAt,
            HydrationStatus: "resolved",
            HydrationReason: null);

    private static PreparedGitHubHydrationRow SampleGitHubRow(string ticketKey, string itemId, DateTimeOffset hydratedAt) =>
        new(
            TicketKey: ticketKey,
            GitHubItemId: itemId,
            Owner: "HL7",
            Repo: "fhir",
            Number: 1,
            Path: null,
            Title: "title",
            State: "open",
            IsPullRequest: false,
            Labels: null,
            UpdatedAt: hydratedAt,
            Url: $"https://github.com/{itemId}",
            HydratedAt: hydratedAt,
            HydrationStatus: "resolved",
            HydrationReason: null);

    private static PreparedRepoHydrationRow SampleRepoRow(string ticketKey, string repo, DateTimeOffset hydratedAt) =>
        new(
            TicketKey: ticketKey,
            Repo: repo,
            Description: "FHIR core spec",
            WorkGroup: null,
            Specification: null,
            CategoryDetail: "FhirCore",
            Url: $"https://github.com/{repo}",
            HydratedAt: hydratedAt,
            HydrationStatus: "resolved",
            HydrationReason: null);

    private static int CountWhere(TestDatabase database, string table, string whereClause)
    {
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {whereClause}";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static TestDatabase CreateDatabase()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "preparer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preparer.db");
        PreparerDatabase database = new(path, NullLogger<PreparerDatabase>.Instance);
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
        SavedAt = DateTimeOffset.Parse("2026-04-29T00:00:00Z"),
        Repos = [new PreparedTicketRepoPayload { Repo = "HL7/fhir", RepoCategory = "FHIR Core", Justification = "repo" }],
        RelatedJiraTickets = [new PreparedTicketRelatedJiraPayload { AssociatedTicketKey = "FHIR-999", LinkType = "related", Justification = "jira" }],
        RelatedZulipThreads = [new PreparedTicketRelatedZulipPayload { ZulipThreadId = "123", Justification = "zulip" }],
        RelatedGitHubItems = [new PreparedTicketRelatedGitHubPayload { GitHubItemId = "HL7/fhir#1", Justification = "github" }],
    };

    private static bool Exists(TestDatabase database, string type, string name)
    {
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = @type AND name = @name";
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@name", name);
        return command.ExecuteScalar() is not null;
    }

    private static bool IsRowIdPrimaryKey(TestDatabase database, string table)
    {
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string columnName = reader.GetString(reader.GetOrdinal("name"));
            int pk = reader.GetInt32(reader.GetOrdinal("pk"));
            if (string.Equals(columnName, "RowId", StringComparison.Ordinal))
            {
                return pk == 1;
            }
        }
        return false;
    }

    private static bool HasUniqueIndexOver(TestDatabase database, string table, string column)
        => HasUniqueIndexOverColumns(database, table, [column]);

    private static bool HasUniqueIndexOverColumns(TestDatabase database, string table, IReadOnlyList<string> expectedColumns)
    {
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand listCommand = connection.CreateCommand();
        listCommand.CommandText = $"PRAGMA index_list({table})";
        List<(string Name, bool Unique)> indexes = [];
        using (SqliteDataReader reader = listCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                string name = reader.GetString(reader.GetOrdinal("name"));
                long unique = reader.GetInt64(reader.GetOrdinal("unique"));
                indexes.Add((name, unique == 1));
            }
        }
        foreach ((string name, bool unique) in indexes)
        {
            if (!unique)
            {
                continue;
            }
            using SqliteCommand info = connection.CreateCommand();
            info.CommandText = $"PRAGMA index_info({name})";
            using SqliteDataReader r = info.ExecuteReader();
            List<string> indexColumns = [];
            while (r.Read())
            {
                indexColumns.Add(r.GetString(r.GetOrdinal("name")));
            }
            if (indexColumns.Count != expectedColumns.Count)
            {
                continue;
            }
            bool allMatch = true;
            for (int i = 0; i < indexColumns.Count; i++)
            {
                if (!string.Equals(indexColumns[i], expectedColumns[i], StringComparison.Ordinal))
                {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch)
            {
                return true;
            }
        }
        return false;
    }

    private static int Count(TestDatabase database, string table)
    {
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(command.ExecuteScalar());
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
