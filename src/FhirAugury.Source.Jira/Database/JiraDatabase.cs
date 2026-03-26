using FhirAugury.Common.Database;
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
        JiraIssueLabelRecord.CreateTable(connection);
        JiraZulipRefRecord.CreateTable(connection);

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

    /// <summary>Drops all tables and recreates the schema from scratch.</summary>
    public void ResetDatabase(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS jira_issues_fts;
            DROP TABLE IF EXISTS jira_comments_fts;
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
            DROP TABLE IF EXISTS jira_zulip_refs;
            """;
        cmd.ExecuteNonQuery();

        InitializeSchema(connection);
    }
}
