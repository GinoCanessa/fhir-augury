using FhirAugury.Common.Database;
using FhirAugury.Common.Database.Records;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Jira.Database;

/// <summary>Jira-specific SQLite database with schema, FTS5, and batch operations.</summary>
public class JiraDatabase : SourceDatabase
{
    private readonly string? _ftsTokenizer;

    public JiraDatabase(string dbPath, ILogger<JiraDatabase> logger, bool readOnly = false, string? ftsTokenizer = null)
        : base(dbPath, logger, readOnly)
    {
        _ftsTokenizer = ftsTokenizer;
    }

    protected override void InitializeSchema(SqliteConnection connection)
    {
        JiraUserRecord.CreateTable(connection);
        JiraIssueRecord.CreateTable(connection);
        JiraCommentRecord.CreateTable(connection);
        JiraIssueLinkRecord.CreateTable(connection);
        JiraIssueRelatedRecord.CreateTable(connection);
        JiraSpecArtifactRecord.CreateTable(connection);
        JiraSyncStateRecord.CreateTable(connection);
        JiraKeywordRecord.CreateTable(connection);
        JiraCorpusKeywordRecord.CreateTable(connection);
        JiraDocStatsRecord.CreateTable(connection);
        JiraIndexWorkGroupRecord.CreateTable(connection);
        JiraIndexSpecificationRecord.CreateTable(connection);
        JiraIndexBallotRecord.CreateTable(connection);
        JiraIndexLabelRecord.CreateTable(connection);
        JiraIndexTypeRecord.CreateTable(connection);
        JiraIndexPriorityRecord.CreateTable(connection);
        JiraIndexStatusRecord.CreateTable(connection);
        JiraIndexResolutionRecord.CreateTable(connection);
        JiraIndexUserRecord.CreateTable(connection);
        JiraIndexInPersonRecord.CreateTable(connection);
        JiraIssueLabelRecord.CreateTable(connection);
        JiraIssueInPersonRecord.CreateTable(connection);
        ZulipXRefRecord.CreateTable(connection);
        GitHubXRefRecord.CreateTable(connection);
        ConfluenceXRefRecord.CreateTable(connection);
        FhirElementXRefRecord.CreateTable(connection);

        MigrateSchema(connection);

        CreateJiraIssuesFts(connection);
        CreateJiraCommentsFts(connection);
    }

    private void CreateJiraIssuesFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "jira_issues_fts",
            contentTable: "jira_issues",
            contentRowId: "Id",
            indexedColumns: ["Title", "DescriptionPlain", "ResolutionDescriptionPlain"],
            tokenizer: _ftsTokenizer);
    }

    private void CreateJiraCommentsFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "jira_comments_fts",
            contentTable: "jira_comments",
            contentRowId: "Id",
            indexedColumns: ["BodyPlain"],
            tokenizer: _ftsTokenizer);
    }

    /// <summary>Rebuilds both FTS5 indexes from their content tables.</summary>
    public void RebuildFtsIndexes(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();
        RebuildFts5(connection, "jira_issues_fts");
        RebuildFts5(connection, "jira_comments_fts");
    }

    /// <summary>
    /// Check if the primary content table of this database is empty
    /// </summary>
    /// <param name="ct"></param>
    /// <returns></returns>
    public bool PrimaryContentTableIsEmpty(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM jira_issues";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 0;
    }

    /// <summary>Drops all tables and recreates the schema from scratch.</summary>
    public void ResetDatabase(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS jira_issues_fts;
            DROP TABLE IF EXISTS jira_comments_fts;
            DROP TABLE IF EXISTS jira_issue_inpersons;
            DROP TABLE IF EXISTS jira_issues;
            DROP TABLE IF EXISTS jira_comments;
            DROP TABLE IF EXISTS jira_issue_links;
            DROP TABLE IF EXISTS jira_issue_related;
            DROP TABLE IF EXISTS jira_issue_labels;
            DROP TABLE IF EXISTS jira_spec_artifacts;
            DROP TABLE IF EXISTS sync_state;
            DROP TABLE IF EXISTS index_keywords;
            DROP TABLE IF EXISTS index_corpus;
            DROP TABLE IF EXISTS index_doc_stats;
            DROP TABLE IF EXISTS jira_index_workgroups;
            DROP TABLE IF EXISTS jira_index_specifications;
            DROP TABLE IF EXISTS jira_index_ballots;
            DROP TABLE IF EXISTS jira_index_labels;
            DROP TABLE IF EXISTS jira_index_types;
            DROP TABLE IF EXISTS jira_index_priorities;
            DROP TABLE IF EXISTS jira_index_statuses;
            DROP TABLE IF EXISTS jira_index_resolutions;
            DROP TABLE IF EXISTS jira_index_users;
            DROP TABLE IF EXISTS jira_index_inpersons;
            DROP TABLE IF EXISTS jira_users;
            DROP TABLE IF EXISTS xref_zulip;
            DROP TABLE IF EXISTS xref_github;
            DROP TABLE IF EXISTS xref_confluence;
            DROP TABLE IF EXISTS xref_fhir_element;
            """;
        cmd.ExecuteNonQuery();

        InitializeSchema(connection);
    }

    /// <summary>Adds new columns to existing tables when upgrading an existing database.</summary>
    private static void MigrateSchema(SqliteConnection connection)
    {
        // FK columns added to jira_issues for user tracking
        string[] newColumns = ["AssigneeId", "ReporterId", "VoteMoverId", "VoteSeconderId"];
        foreach (string col in newColumns)
        {
            if (!ColumnExists(connection, "jira_issues", col))
            {
                using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText = $"ALTER TABLE jira_issues ADD COLUMN {col} INTEGER";
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
