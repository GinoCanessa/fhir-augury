using FhirAugury.Common.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Jira.Database;

/// <summary>Jira-specific SQLite database with schema, FTS5, and batch operations.</summary>
public class JiraDatabase : SourceDatabase
{
    public JiraDatabase(string dbPath, ILogger<JiraDatabase> logger, bool readOnly = false)
        : base(dbPath, logger, readOnly) { }

    protected override void InitializeSchema(SqliteConnection connection)
    {
        JiraIssueRecord.CreateTable(connection);
        JiraCommentRecord.CreateTable(connection);
        JiraIssueLinkRecord.CreateTable(connection);
        JiraSpecArtifactRecord.CreateTable(connection);
        JiraSyncStateRecord.CreateTable(connection);
        JiraKeywordRecord.CreateTable(connection);
        JiraCorpusKeywordRecord.CreateTable(connection);
        JiraDocStatsRecord.CreateTable(connection);

        CreateJiraIssuesFts(connection);
        CreateJiraCommentsFts(connection);
    }

    private static void CreateJiraIssuesFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "jira_issues_fts",
            contentTable: "jira_issues",
            contentRowId: "Id",
            indexedColumns: ["Title", "Description", "Labels", "WorkGroup", "Specification"]);
    }

    private static void CreateJiraCommentsFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "jira_comments_fts",
            contentTable: "jira_comments",
            contentRowId: "Id",
            indexedColumns: ["Body"]);
    }

    /// <summary>Rebuilds both FTS5 indexes from their content tables.</summary>
    public void RebuildFtsIndexes()
    {
        using var connection = OpenConnection();
        RebuildFts5(connection, "jira_issues_fts");
        RebuildFts5(connection, "jira_comments_fts");
    }

    /// <summary>Drops all tables and recreates the schema from scratch.</summary>
    public void ResetDatabase()
    {
        using var connection = OpenConnection();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS jira_issues_fts;
            DROP TABLE IF EXISTS jira_comments_fts;
            DROP TABLE IF EXISTS jira_issues;
            DROP TABLE IF EXISTS jira_comments;
            DROP TABLE IF EXISTS jira_issue_links;
            DROP TABLE IF EXISTS jira_spec_artifacts;
            DROP TABLE IF EXISTS sync_state;
            DROP TABLE IF EXISTS index_keywords;
            DROP TABLE IF EXISTS index_corpus;
            DROP TABLE IF EXISTS index_doc_stats;
            """;
        cmd.ExecuteNonQuery();

        InitializeSchema(connection);
    }
}
