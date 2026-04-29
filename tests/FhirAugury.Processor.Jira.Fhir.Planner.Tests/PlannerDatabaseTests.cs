using FhirAugury.Processor.Jira.Fhir.Planner.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannerDatabaseTests
{
    [Fact]
    public void Initialize_CreatesPlannerTables()
    {
        using DatabaseFixture fixture = new();

        Assert.True(TableExists(fixture.Database, "planned_tickets"));
        Assert.True(TableExists(fixture.Database, "planned_ticket_repos"));
        Assert.True(TableExists(fixture.Database, "planned_ticket_repo_changes"));
        Assert.True(TableExists(fixture.Database, "planned_ticket_repo_impacts"));
        Assert.True(TableExists(fixture.Database, "planned_ticket_change_validations"));
        Assert.True(TableExists(fixture.Database, "planned_ticket_testing_considerations"));
        Assert.True(TableExists(fixture.Database, "planned_ticket_open_questions"));
        Assert.Contains("RepoRevision", Columns(fixture.Database, "planned_ticket_repos"));
        Assert.Contains("ReplacementLines", Columns(fixture.Database, "planned_ticket_repo_changes"));
    }

    [Fact]
    public async Task InsertAndSelect_PlannedTicketGraph_RoundTrips()
    {
        using DatabaseFixture fixture = new();
        await InsertPlanAsync(fixture.Database, "FHIR-123", "repo-1", "change-1");

        Assert.Equal(1, Count(fixture.Database, "planned_tickets", "Key = 'FHIR-123'"));
        Assert.Equal(1, Count(fixture.Database, "planned_ticket_repos", "IssueKey = 'FHIR-123' AND RepoRevision = 'abc123'"));
        Assert.Equal(1, Count(fixture.Database, "planned_ticket_repo_changes", "IssueKey = 'FHIR-123' AND SourceLineStart = 10 AND SourceLineEnd = 12"));
        Assert.Equal(1, Count(fixture.Database, "planned_ticket_repo_impacts", "IssueKey = 'FHIR-123' AND TicketRepoChangeId = 'change-1'"));
        Assert.Equal(1, Count(fixture.Database, "planned_ticket_change_validations", "IssueKey = 'FHIR-123'"));
        Assert.Equal(1, Count(fixture.Database, "planned_ticket_testing_considerations", "IssueKey = 'FHIR-123'"));
        Assert.Equal(1, Count(fixture.Database, "planned_ticket_open_questions", "IssueKey = 'FHIR-123'"));
    }

    [Fact]
    public void ReplacementLines_AreJsonArrayText()
    {
        string json = ReplacementLineJson.Serialize(["first", "second"]);
        string[] lines = ReplacementLineJson.Deserialize(json);

        Assert.Equal("[\"first\",\"second\"]", json);
        Assert.Equal(["first", "second"], lines);
    }

    [Fact]
    public async Task DeletePlanForTicket_RemovesDependentRowsOnlyForThatTicket()
    {
        using DatabaseFixture fixture = new();
        await InsertPlanAsync(fixture.Database, "FHIR-123", "repo-1", "change-1");
        await InsertPlanAsync(fixture.Database, "FHIR-456", "repo-2", "change-2");

        await fixture.Database.DeletePlanForTicketAsync("FHIR-123");

        Assert.Equal(0, Count(fixture.Database, "planned_tickets", "Key = 'FHIR-123'"));
        Assert.Equal(0, Count(fixture.Database, "planned_ticket_repos", "IssueKey = 'FHIR-123'"));
        Assert.Equal(0, Count(fixture.Database, "planned_ticket_repo_changes", "IssueKey = 'FHIR-123'"));
        Assert.Equal(0, Count(fixture.Database, "planned_ticket_repo_impacts", "IssueKey = 'FHIR-123'"));
        Assert.Equal(0, Count(fixture.Database, "planned_ticket_change_validations", "IssueKey = 'FHIR-123'"));
        Assert.Equal(0, Count(fixture.Database, "planned_ticket_testing_considerations", "IssueKey = 'FHIR-123'"));
        Assert.Equal(0, Count(fixture.Database, "planned_ticket_open_questions", "IssueKey = 'FHIR-123'"));
        Assert.Equal(1, Count(fixture.Database, "planned_tickets", "Key = 'FHIR-456'"));
        Assert.Equal(1, Count(fixture.Database, "planned_ticket_repo_changes", "IssueKey = 'FHIR-456'"));
    }

    private static async Task InsertPlanAsync(PlannerDatabase database, string issueKey, string repoId, string changeId)
    {
        await using SqliteConnection connection = database.OpenConnection();
        await ExecuteAsync(connection, "INSERT INTO planned_tickets (Id, Key, Resolution, ResolutionSummary, FeatureProposal, DesignRationale, SavedAt) VALUES (@id, @key, @resolution, @summary, @proposal, @rationale, @savedAt)",
            ("@id", issueKey + "-ticket"), ("@key", issueKey), ("@resolution", "raw"), ("@summary", "summary"), ("@proposal", "proposal"), ("@rationale", "rationale"), ("@savedAt", DateTimeOffset.UtcNow.ToString("O")));
        await ExecuteAsync(connection, "INSERT INTO planned_ticket_repos (Id, IssueKey, RepoKey, RepoRevision, Justification) VALUES (@id, @key, @repo, @revision, @justification)",
            ("@id", repoId), ("@key", issueKey), ("@repo", "HL7/fhir"), ("@revision", "abc123"), ("@justification", "because"));
        await ExecuteAsync(connection, "INSERT INTO planned_ticket_repo_changes (Id, IssueKey, TicketRepoId, RepoKey, ChangeSequence, FilePath, ChangeTitle, ChangeDescription, SourceLineStart, SourceLineEnd, ReplacementLines, Reason) VALUES (@id, @key, @repoId, @repo, @sequence, @file, @title, @description, @start, @end, @replacement, @reason)",
            ("@id", changeId), ("@key", issueKey), ("@repoId", repoId), ("@repo", "HL7/fhir"), ("@sequence", 1), ("@file", "source/observation.html"), ("@title", "title"), ("@description", "description"), ("@start", 10), ("@end", 12), ("@replacement", ReplacementLineJson.Serialize(["line"])), ("@reason", "reason"));
        await ExecuteAsync(connection, "INSERT INTO planned_ticket_repo_impacts (Id, IssueKey, TicketRepoId, RepoKey, TicketRepoChangeId, AffectedFilePath, HowAffected) VALUES (@id, @key, @repoId, @repo, @changeId, @file, @how)",
            ("@id", issueKey + "-impact"), ("@key", issueKey), ("@repoId", repoId), ("@repo", "HL7/fhir"), ("@changeId", changeId), ("@file", "source/observation.profile.xml"), ("@how", "affected"));
        await ExecuteAsync(connection, "INSERT INTO planned_ticket_change_validations (Id, IssueKey, TicketRepoId, RepoKey, ValidationSequence, Action) VALUES (@id, @key, @repoId, @repo, @sequence, @action)",
            ("@id", issueKey + "-validation"), ("@key", issueKey), ("@repoId", repoId), ("@repo", "HL7/fhir"), ("@sequence", 1), ("@action", "build"));
        await ExecuteAsync(connection, "INSERT INTO planned_ticket_testing_considerations (Id, IssueKey, TicketRepoId, RepoKey, ConsiderationSequence, Consideration) VALUES (@id, @key, @repoId, @repo, @sequence, @consideration)",
            ("@id", issueKey + "-testing"), ("@key", issueKey), ("@repoId", repoId), ("@repo", "HL7/fhir"), ("@sequence", 1), ("@consideration", "test it"));
        await ExecuteAsync(connection, "INSERT INTO planned_ticket_open_questions (Id, IssueKey, TicketRepoId, RepoKey, QuestionSequence, Question) VALUES (@id, @key, @repoId, @repo, @sequence, @question)",
            ("@id", issueKey + "-question"), ("@key", issueKey), ("@repoId", repoId), ("@repo", "HL7/fhir"), ("@sequence", 1), ("@question", "question?"));
    }

    private static bool TableExists(PlannerDatabase database, string tableName) => Count(database, "sqlite_master", $"type = 'table' AND name = '{tableName}'") == 1;

    private static IReadOnlyList<string> Columns(PlannerDatabase database, string tableName)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> columns = [];
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static int Count(PlannerDatabase database, string tableName, string where)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName} WHERE {where}";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _directory;

        public DatabaseFixture()
        {
            _directory = Path.Combine(Environment.CurrentDirectory, "temp", "planner-database-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Database = new PlannerDatabase(Path.Combine(_directory, "planner.db"), NullLogger<PlannerDatabase>.Instance);
            Database.Initialize();
        }

        public PlannerDatabase Database { get; }

        public void Dispose()
        {
            Database.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
