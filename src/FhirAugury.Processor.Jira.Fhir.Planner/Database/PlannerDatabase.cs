using FhirAugury.Processing.Jira.Common.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Database;

public sealed class PlannerDatabase(string dbPath, ILogger<PlannerDatabase> logger, bool readOnly = false)
    : FhirAugury.Processing.Common.Database.ProcessingDatabase(dbPath, logger, readOnly)
{
    public string DatabasePath { get; } = dbPath;

    protected override void InitializeSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = JiraProcessingSourceTicketStore.SchemaSql + PlannerSchemaSql;
        command.ExecuteNonQuery();
    }

    public async Task DeletePlanForTicketAsync(string issueKey, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await DeletePlanForTicketAsync(connection, issueKey, ct);
    }

    public static async Task DeletePlanForTicketAsync(SqliteConnection connection, string issueKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issueKey);
        foreach (string table in DeleteOrder)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = table == "planned_tickets"
                ? $"DELETE FROM {table} WHERE Key = @issueKey"
                : $"DELETE FROM {table} WHERE IssueKey = @issueKey";
            command.Parameters.Add(new SqliteParameter("@issueKey", issueKey));
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<bool> PlanExistsAsync(string issueKey, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM planned_tickets WHERE Key = @issueKey LIMIT 1";
        command.Parameters.Add(new SqliteParameter("@issueKey", issueKey));
        object? value = await command.ExecuteScalarAsync(ct);
        return value is not null;
    }

    private static readonly string[] DeleteOrder =
    [
        "planned_ticket_open_questions",
        "planned_ticket_testing_considerations",
        "planned_ticket_change_validations",
        "planned_ticket_repo_impacts",
        "planned_ticket_repo_changes",
        "planned_ticket_repos",
        "planned_tickets",
    ];

    private const string PlannerSchemaSql = """
        CREATE TABLE IF NOT EXISTS planned_tickets (
            Id TEXT NOT NULL PRIMARY KEY,
            Key TEXT NOT NULL UNIQUE,
            Resolution TEXT NOT NULL,
            ResolutionSummary TEXT NOT NULL,
            FeatureProposal TEXT NOT NULL,
            DesignRationale TEXT NOT NULL,
            SavedAt TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_planned_tickets_key ON planned_tickets(Key);

        CREATE TABLE IF NOT EXISTS planned_ticket_repos (
            Id TEXT NOT NULL PRIMARY KEY,
            IssueKey TEXT NOT NULL,
            RepoKey TEXT NOT NULL,
            RepoRevision TEXT NULL,
            Justification TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_repos_issue_key ON planned_ticket_repos(IssueKey);
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_repos_repo_key ON planned_ticket_repos(RepoKey);

        CREATE TABLE IF NOT EXISTS planned_ticket_repo_changes (
            Id TEXT NOT NULL PRIMARY KEY,
            IssueKey TEXT NOT NULL,
            TicketRepoId TEXT NOT NULL,
            RepoKey TEXT NOT NULL,
            ChangeSequence INTEGER NOT NULL,
            FilePath TEXT NOT NULL,
            ChangeTitle TEXT NOT NULL,
            ChangeDescription TEXT NOT NULL,
            SourceLineStart INTEGER NULL,
            SourceLineEnd INTEGER NULL,
            ReplacementLines TEXT NOT NULL,
            Reason TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_repo_changes_issue_key ON planned_ticket_repo_changes(IssueKey);
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_repo_changes_ticket_repo_id ON planned_ticket_repo_changes(TicketRepoId);
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_repo_changes_repo_key ON planned_ticket_repo_changes(RepoKey);
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_repo_changes_file_path ON planned_ticket_repo_changes(FilePath);

        CREATE TABLE IF NOT EXISTS planned_ticket_repo_impacts (
            Id TEXT NOT NULL PRIMARY KEY,
            IssueKey TEXT NOT NULL,
            TicketRepoId TEXT NOT NULL,
            RepoKey TEXT NOT NULL,
            TicketRepoChangeId TEXT NULL,
            AffectedFilePath TEXT NOT NULL,
            HowAffected TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_repo_impacts_issue_key ON planned_ticket_repo_impacts(IssueKey);
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_repo_impacts_ticket_repo_id ON planned_ticket_repo_impacts(TicketRepoId);
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_repo_impacts_repo_key ON planned_ticket_repo_impacts(RepoKey);
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_repo_impacts_change_id ON planned_ticket_repo_impacts(TicketRepoChangeId);

        CREATE TABLE IF NOT EXISTS planned_ticket_change_validations (
            Id TEXT NOT NULL PRIMARY KEY,
            IssueKey TEXT NOT NULL,
            TicketRepoId TEXT NOT NULL,
            RepoKey TEXT NOT NULL,
            ValidationSequence INTEGER NOT NULL,
            Action TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_change_validations_issue_key ON planned_ticket_change_validations(IssueKey);
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_change_validations_ticket_repo_id ON planned_ticket_change_validations(TicketRepoId);
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_change_validations_repo_key ON planned_ticket_change_validations(RepoKey);

        CREATE TABLE IF NOT EXISTS planned_ticket_testing_considerations (
            Id TEXT NOT NULL PRIMARY KEY,
            IssueKey TEXT NOT NULL,
            TicketRepoId TEXT NOT NULL,
            RepoKey TEXT NOT NULL,
            ConsiderationSequence INTEGER NOT NULL,
            Consideration TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_testing_considerations_issue_key ON planned_ticket_testing_considerations(IssueKey);
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_testing_considerations_ticket_repo_id ON planned_ticket_testing_considerations(TicketRepoId);
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_testing_considerations_repo_key ON planned_ticket_testing_considerations(RepoKey);

        CREATE TABLE IF NOT EXISTS planned_ticket_open_questions (
            Id TEXT NOT NULL PRIMARY KEY,
            IssueKey TEXT NOT NULL,
            TicketRepoId TEXT NOT NULL,
            RepoKey TEXT NOT NULL,
            QuestionSequence INTEGER NOT NULL,
            Question TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_open_questions_issue_key ON planned_ticket_open_questions(IssueKey);
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_open_questions_ticket_repo_id ON planned_ticket_open_questions(TicketRepoId);
        CREATE INDEX IF NOT EXISTS idx_planned_ticket_open_questions_repo_key ON planned_ticket_open_questions(RepoKey);
        """;
}
