using FhirAugury.Common.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Database;

/// <summary>GitHub-specific SQLite database with schema, FTS5, and batch operations.</summary>
public class GitHubDatabase : SourceDatabase
{
    public GitHubDatabase(string dbPath, ILogger<GitHubDatabase> logger, bool readOnly = false)
        : base(dbPath, logger, readOnly) { }

    protected override void InitializeSchema(SqliteConnection connection)
    {
        GitHubRepoRecord.CreateTable(connection);
        GitHubIssueRecord.CreateTable(connection);
        GitHubCommentRecord.CreateTable(connection);
        GitHubCommitRecord.CreateTable(connection);
        GitHubCommitFileRecord.CreateTable(connection);
        GitHubCommitPrLinkRecord.CreateTable(connection);
        GitHubJiraRefRecord.CreateTable(connection);
        GitHubSpecFileMapRecord.CreateTable(connection);
        GitHubSyncStateRecord.CreateTable(connection);
        GitHubKeywordRecord.CreateTable(connection);
        GitHubCorpusKeywordRecord.CreateTable(connection);
        GitHubDocStatsRecord.CreateTable(connection);

        CreateGitHubIssuesFts(connection);
        CreateGitHubCommentsFts(connection);
        CreateGitHubCommitsFts(connection);
    }

    private static void CreateGitHubIssuesFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "github_issues_fts",
            contentTable: "github_issues",
            contentRowId: "Id",
            indexedColumns: ["Title", "Body"]);
    }

    private static void CreateGitHubCommentsFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "github_comments_fts",
            contentTable: "github_comments",
            contentRowId: "Id",
            indexedColumns: ["Body"]);
    }

    private static void CreateGitHubCommitsFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "github_commits_fts",
            contentTable: "github_commits",
            contentRowId: "Id",
            indexedColumns: ["Message"]);
    }

    /// <summary>Rebuilds all FTS5 indexes from their content tables.</summary>
    public void RebuildFtsIndexes()
    {
        using var connection = OpenConnection();
        RebuildFts5(connection, "github_issues_fts");
        RebuildFts5(connection, "github_comments_fts");
        RebuildFts5(connection, "github_commits_fts");
    }

    /// <summary>Drops all tables and recreates the schema from scratch.</summary>
    public void ResetDatabase()
    {
        using var connection = OpenConnection();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS github_issues_fts;
            DROP TABLE IF EXISTS github_comments_fts;
            DROP TABLE IF EXISTS github_commits_fts;
            DROP TABLE IF EXISTS github_repos;
            DROP TABLE IF EXISTS github_issues;
            DROP TABLE IF EXISTS github_comments;
            DROP TABLE IF EXISTS github_commits;
            DROP TABLE IF EXISTS github_commit_files;
            DROP TABLE IF EXISTS github_commit_pr_links;
            DROP TABLE IF EXISTS github_jira_refs;
            DROP TABLE IF EXISTS github_spec_file_map;
            DROP TABLE IF EXISTS sync_state;
            DROP TABLE IF EXISTS index_keywords;
            DROP TABLE IF EXISTS index_corpus;
            DROP TABLE IF EXISTS index_doc_stats;
            """;
        cmd.ExecuteNonQuery();

        InitializeSchema(connection);
    }
}
