using FhirAugury.Common.Database;
using FhirAugury.Common.Database.Records;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Database;

/// <summary>GitHub-specific SQLite database with schema, FTS5, and batch operations.</summary>
public class GitHubDatabase : SourceDatabase
{
    private readonly string? _ftsTokenizer;

    public GitHubDatabase(string dbPath, ILogger<GitHubDatabase> logger, bool readOnly = false, string? ftsTokenizer = null)
        : base(dbPath, logger, readOnly)
    {
        _ftsTokenizer = ftsTokenizer;
    }

    protected override void InitializeSchema(SqliteConnection connection)
    {
        // Legacy-schema column migrations MUST run before the generated
        // CreateTable calls below: several generated CreateTable bodies issue
        // CREATE INDEX over these columns, which errors on a pre-feature table
        // under DQS-off SQLite (SourceGear) when the column is not yet present.
        // AddColumnIfMissing no-ops on a fresh DB (table absent); CreateTable
        // then builds the full schema including these columns and their indexes.
        SqliteSchemaHelpers.AddColumnIfMissing(connection, "jira_workgroups", "WorkGroupCode", "TEXT");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, "github_spec_file_map", "WorkGroup", "TEXT");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, "github_spec_file_map", "WorkGroupRaw", "TEXT");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, "github_canonical_artifacts", "WorkGroupRaw", "TEXT");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, "github_structure_definitions", "WorkGroupRaw", "TEXT");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, "github_repos", "DefaultBranch", "TEXT");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, "github_comments", "ExternalId", "TEXT");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, "github_comments", "CommentKind", "TEXT");

        GitHubRepoRecord.CreateTable(connection);
        GitHubIssueRecord.CreateTable(connection);
        GitHubCommentRecord.CreateTable(connection);
        GitHubCommitRecord.CreateTable(connection);
        GitHubCommitFileRecord.CreateTable(connection);
        GitHubCommitPrLinkRecord.CreateTable(connection);
        GitHubPrTicketLinkRecord.CreateTable(connection);
        JiraXRefRecord.CreateTable(connection);
        ZulipXRefRecord.CreateTable(connection);
        ConfluenceXRefRecord.CreateTable(connection);
        FhirElementXRefRecord.CreateTable(connection);
        GitHubSpecFileMapRecord.CreateTable(connection);
        GitHubSyncStateRecord.CreateTable(connection);
        GitHubKeywordRecord.CreateTable(connection);
        GitHubCorpusKeywordRecord.CreateTable(connection);
        GitHubDocStatsRecord.CreateTable(connection);
        GitHubFileContentRecord.CreateTable(connection);
        GitHubFileTagRecord.CreateTable(connection);
        GitHubStructureDefinitionRecord.CreateTable(connection);
        GitHubSdElementRecord.CreateTable(connection);
        GitHubCanonicalArtifactRecord.CreateTable(connection);
        JiraSpecRecord.CreateTable(connection);
        JiraSpecVersionRecord.CreateTable(connection);
        JiraSpecArtifactRecord.CreateTable(connection);
        JiraSpecPageRecord.CreateTable(connection);
        JiraWorkgroupRecord.CreateTable(connection);
        JiraSpecFamilyRecord.CreateTable(connection);
        Hl7WorkGroupRecord.CreateTable(connection);
        GitHubRepoWorkGroupRecord.CreateTable(connection);

        // Phase 3 migration: index on WorkGroupCode (column added above, before
        // the generated CreateTable) to the existing jira_workgroups table.
        using (SqliteCommand idx = connection.CreateCommand())
        {
            idx.CommandText = "CREATE INDEX IF NOT EXISTS ix_jira_workgroups_WorkGroupCode ON jira_workgroups(WorkGroupCode)";
            idx.ExecuteNonQuery();
        }

        // Phase 4 migrations: indexes on WorkGroup / WorkGroupRaw (columns added
        // above, before the generated CreateTable) for in-place upgrades.
        using (SqliteCommand idx = connection.CreateCommand())
        {
            idx.CommandText = """
                CREATE INDEX IF NOT EXISTS ix_github_spec_file_map_RepoFullName_WorkGroup
                    ON github_spec_file_map(RepoFullName, WorkGroup);
                CREATE INDEX IF NOT EXISTS ix_github_spec_file_map_RepoFullName_WorkGroupRaw
                    ON github_spec_file_map(RepoFullName, WorkGroupRaw);
                CREATE INDEX IF NOT EXISTS ix_github_canonical_artifacts_RepoFullName_WorkGroupRaw
                    ON github_canonical_artifacts(RepoFullName, WorkGroupRaw);
                CREATE INDEX IF NOT EXISTS ix_github_structure_definitions_RepoFullName_WorkGroupRaw
                    ON github_structure_definitions(RepoFullName, WorkGroupRaw);
                """;
            idx.ExecuteNonQuery();
        }

        // Idempotency: github_commit_pr_links PK is a GetIndex() value, so
        // INSERT OR IGNORE only dedupes against a real UNIQUE constraint. This
        // manual unique index over the natural key makes ignoreDuplicates inserts
        // idempotent (created after CreateTable; no migrated columns involved).
        using (SqliteCommand idx = connection.CreateCommand())
        {
            idx.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS ix_github_commit_pr_links_natural
                    ON github_commit_pr_links(CommitSha, PrNumber, RepoFullName);
                """;
            idx.ExecuteNonQuery();
        }

        // Idempotency for comments: github_comments PK is a GetIndex() value, so
        // dedup across (re-)ingestion needs a real UNIQUE constraint over the
        // GitHub-native comment identity. CommentKind discriminates the three
        // comment resources (issue / review / review_comment) whose numeric id
        // spaces overlap. Built after CreateTable, with ExternalId/CommentKind
        // already added via AddColumnIfMissing above.
        using (SqliteCommand idx = connection.CreateCommand())
        {
            idx.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS ix_github_comments_external
                    ON github_comments(RepoFullName, CommentKind, ExternalId);
                """;
            idx.ExecuteNonQuery();
        }

        // Idempotency for PR↔ticket edges: github_pr_ticket_links PK is a
        // GetIndex() value, so INSERT OR IGNORE only dedupes against a real
        // UNIQUE constraint. This manual unique index over the natural key makes
        // ignoreDuplicates inserts idempotent (created after CreateTable; no
        // migrated columns involved).
        using (SqliteCommand idx = connection.CreateCommand())
        {
            idx.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS ix_github_pr_ticket_links_natural
                    ON github_pr_ticket_links(RepoFullName, PrNumber, JiraKey);
                """;
            idx.ExecuteNonQuery();
        }

        CreateGitHubIssuesFts(connection);
        CreateGitHubCommentsFts(connection);
        CreateGitHubCommitsFts(connection);
        CreateGitHubFileContentsFts(connection);
        CreateGitHubStructureDefinitionsFts(connection);
        CreateGitHubCanonicalArtifactsFts(connection);
    }

    private void CreateGitHubIssuesFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "github_issues_fts",
            contentTable: "github_issues",
            contentRowId: "Id",
            indexedColumns: ["Title", "Body"],
            tokenizer: _ftsTokenizer);
    }

    private void CreateGitHubCommentsFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "github_comments_fts",
            contentTable: "github_comments",
            contentRowId: "Id",
            indexedColumns: ["Body"],
            tokenizer: _ftsTokenizer);
    }

    private void CreateGitHubCommitsFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "github_commits_fts",
            contentTable: "github_commits",
            contentRowId: "Id",
            indexedColumns: ["Message", "Body"],
            tokenizer: _ftsTokenizer);
    }

    private void CreateGitHubFileContentsFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "github_file_contents_fts",
            contentTable: "github_file_contents",
            contentRowId: "Id",
            indexedColumns: ["ContentText", "FilePath"],
            tokenizer: _ftsTokenizer);
    }

    private void CreateGitHubStructureDefinitionsFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "github_structure_definitions_fts",
            contentTable: "github_structure_definitions",
            contentRowId: "Id",
            indexedColumns: ["Name", "Title", "Description"],
            tokenizer: _ftsTokenizer);
    }

    private void CreateGitHubCanonicalArtifactsFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "github_canonical_artifacts_fts",
            contentTable: "github_canonical_artifacts",
            contentRowId: "Id",
            indexedColumns: ["Name", "Title", "Description", "Url"],
            tokenizer: _ftsTokenizer);
    }

    /// <summary>Rebuilds all FTS5 indexes from their content tables.</summary>
    public void RebuildFtsIndexes()
    {
        using SqliteConnection connection = OpenConnection();
        RebuildFts5(connection, "github_issues_fts");
        RebuildFts5(connection, "github_comments_fts");
        RebuildFts5(connection, "github_commits_fts");
        RebuildFts5(connection, "github_file_contents_fts");
        RebuildFts5(connection, "github_structure_definitions_fts");
        RebuildFts5(connection, "github_canonical_artifacts_fts");
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
        cmd.CommandText = "SELECT COUNT(*) FROM github_commits";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 0;
    }

    /// <summary>Drops all tables and recreates the schema from scratch.</summary>
    public void ResetDatabase()
    {
        using SqliteConnection connection = OpenConnection();

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS github_issues_fts;
            DROP TABLE IF EXISTS github_comments_fts;
            DROP TABLE IF EXISTS github_commits_fts;
            DROP TABLE IF EXISTS github_file_contents_fts;
            DROP TABLE IF EXISTS github_structure_definitions_fts;
            DROP TABLE IF EXISTS github_sd_elements;
            DROP TABLE IF EXISTS github_structure_definitions;
            DROP TABLE IF EXISTS github_canonical_artifacts_fts;
            DROP TABLE IF EXISTS github_repos;
            DROP TABLE IF EXISTS github_issues;
            DROP TABLE IF EXISTS github_comments;
            DROP TABLE IF EXISTS github_commits;
            DROP TABLE IF EXISTS github_commit_files;
            DROP TABLE IF EXISTS github_commit_pr_links;
            DROP TABLE IF EXISTS github_pr_ticket_links;
            DROP TABLE IF EXISTS xref_jira;
            DROP TABLE IF EXISTS xref_zulip;
            DROP TABLE IF EXISTS xref_confluence;
            DROP TABLE IF EXISTS xref_fhir_element;
            DROP TABLE IF EXISTS github_spec_file_map;
            DROP TABLE IF EXISTS github_file_contents;
            DROP TABLE IF EXISTS github_file_tags;
            DROP TABLE IF EXISTS github_canonical_artifacts;
            DROP TABLE IF EXISTS jira_spec_versions;
            DROP TABLE IF EXISTS jira_spec_artifacts;
            DROP TABLE IF EXISTS jira_spec_pages;
            DROP TABLE IF EXISTS jira_spec_families;
            DROP TABLE IF EXISTS jira_workgroups;
            DROP TABLE IF EXISTS jira_specs;
            DROP TABLE IF EXISTS github_repo_workgroups;
            DROP TABLE IF EXISTS sync_state;
            DROP TABLE IF EXISTS index_keywords;
            DROP TABLE IF EXISTS index_corpus;
            DROP TABLE IF EXISTS index_doc_stats;
            """;
        cmd.ExecuteNonQuery();

        InitializeSchema(connection);
    }
}
