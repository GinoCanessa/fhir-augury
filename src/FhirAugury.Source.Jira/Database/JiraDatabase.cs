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
        JiraProjectRecord.CreateTable(connection);
        JiraIssueRecord.CreateTable(connection);
        JiraProjectScopeStatementRecord.CreateTable(connection);
        JiraBaldefRecord.CreateTable(connection);
        JiraBallotRecord.CreateTable(connection);
        JiraCommentRecord.CreateTable(connection);
        JiraIssueLinkRecord.CreateTable(connection);
        JiraIssueRelatedRecord.CreateTable(connection);
        JiraSyncStateRecord.CreateTable(connection);
        JiraKeywordRecord.CreateTable(connection);
        JiraCorpusKeywordRecord.CreateTable(connection);
        JiraDocStatsRecord.CreateTable(connection);
        Hl7WorkGroupRecord.CreateTable(connection);
        JiraIndexWorkGroupRecord.CreateTable(connection);
        JiraIndexSpecificationRecord.CreateTable(connection);
        JiraIndexBallotTargetRecord.CreateTable(connection);
        JiraIndexBallotCycleRecord.CreateTable(connection);
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

        CreateJiraIssuesFts(connection);
        CreateJiraCommentsFts(connection);
        CreateJiraPssFts(connection);
        CreateJiraBaldefFts(connection);
        CreateJiraBallotFts(connection);

        MigrateSchema(connection);
    }

    /// <summary>
    /// Applies schema migrations for tables/columns added after initial
    /// release. Safe to call repeatedly; each migration checks before altering.
    /// </summary>
    private static void MigrateSchema(SqliteConnection connection)
    {
        // Migration: add jira_projects table for older databases that did
        // not include it at initial schema creation. CreateTable is
        // idempotent (CREATE TABLE IF NOT EXISTS), so this is safe.
        JiraProjectRecord.CreateTable(connection);

        // Migration: add hl7_workgroups (FR 02) for older databases.
        Hl7WorkGroupRecord.CreateTable(connection);

        // Migration (FR 03): expand jira_index_workgroups with the FK to
        // hl7_workgroups and per-status bucket columns. SQLite has no
        // ADD COLUMN IF NOT EXISTS, so we gate each ALTER on PRAGMA
        // table_info. The next index rebuild populates real values; until
        // then existing rows have all-zero buckets and a null FK.
        AddColumnIfMissing(connection, "jira_index_workgroups", "WorkGroupId",                "INTEGER");
        AddColumnIfMissing(connection, "jira_index_workgroups", "IssueCountSubmitted",        "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "jira_index_workgroups", "IssueCountTriaged",          "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "jira_index_workgroups", "IssueCountWaitingForInput",  "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "jira_index_workgroups", "IssueCountNoChange",         "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "jira_index_workgroups", "IssueCountChangeRequired",   "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "jira_index_workgroups", "IssueCountPublished",        "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "jira_index_workgroups", "IssueCountApplied",          "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "jira_index_workgroups", "IssueCountDuplicate",        "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "jira_index_workgroups", "IssueCountClosed",           "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "jira_index_workgroups", "IssueCountBalloted",         "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "jira_index_workgroups", "IssueCountWithdrawn",        "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "jira_index_workgroups", "IssueCountDeferred",         "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "jira_index_workgroups", "IssueCountOther",            "INTEGER NOT NULL DEFAULT 0");
    }

    /// <summary>
    /// Issues <c>ALTER TABLE ... ADD COLUMN</c> only when the column is not
    /// already present. SQLite lacks <c>IF NOT EXISTS</c> on ADD COLUMN so we
    /// inspect <c>PRAGMA table_info</c> first.
    /// </summary>
    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string typeAndDefault)
    {
        HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table})";
            using SqliteDataReader r = info.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(1));
        }

        if (existing.Contains(column)) return;

        using SqliteCommand alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAndDefault}";
        alter.ExecuteNonQuery();
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

    private void CreateJiraPssFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "jira_pss_fts",
            contentTable: "jira_pss",
            contentRowId: "Id",
            indexedColumns: ["Title", "DescriptionPlain", "ProjectDescriptionPlain"],
            tokenizer: _ftsTokenizer);
    }

    private void CreateJiraBaldefFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "jira_baldef_fts",
            contentTable: "jira_baldef",
            contentRowId: "Id",
            indexedColumns: ["Title", "DescriptionPlain"],
            tokenizer: _ftsTokenizer);
    }

    private void CreateJiraBallotFts(SqliteConnection connection)
    {
        // BALLOT rows are vote-tracking rows; <description> is empty per
        // plan §2.2. The negative-with-comment narrative lives on the
        // related FHIR-* ticket (already in jira_issues_fts), so only
        // Title (parsed summary) is indexed here.
        CreateFts5Table(
            connection,
            ftsTableName: "jira_ballot_fts",
            contentTable: "jira_ballot",
            contentRowId: "Id",
            indexedColumns: ["Title"],
            tokenizer: _ftsTokenizer);
    }

    /// <summary>Rebuilds all FTS5 indexes from their content tables.</summary>
    public void RebuildFtsIndexes(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();
        RebuildFts5(connection, "jira_issues_fts");
        RebuildFts5(connection, "jira_comments_fts");
        RebuildFts5(connection, "jira_pss_fts");
        RebuildFts5(connection, "jira_baldef_fts");
        RebuildFts5(connection, "jira_ballot_fts");
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
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM jira_issues) +
                (SELECT COUNT(*) FROM jira_pss) +
                (SELECT COUNT(*) FROM jira_baldef) +
                (SELECT COUNT(*) FROM jira_ballot)
            """;
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
            DROP TABLE IF EXISTS jira_pss_fts;
            DROP TABLE IF EXISTS jira_baldef_fts;
            DROP TABLE IF EXISTS jira_ballot_fts;
            DROP TABLE IF EXISTS jira_issue_inpersons;
            DROP TABLE IF EXISTS jira_issues;
            DROP TABLE IF EXISTS jira_pss;
            DROP TABLE IF EXISTS jira_baldef;
            DROP TABLE IF EXISTS jira_ballot;
            DROP TABLE IF EXISTS jira_comments;
            DROP TABLE IF EXISTS jira_issue_links;
            DROP TABLE IF EXISTS jira_issue_related;
            DROP TABLE IF EXISTS jira_issue_labels;
            DROP TABLE IF EXISTS sync_state;
            DROP TABLE IF EXISTS index_keywords;
            DROP TABLE IF EXISTS index_corpus;
            DROP TABLE IF EXISTS index_doc_stats;
            DROP TABLE IF EXISTS jira_index_workgroups;
            DROP TABLE IF EXISTS jira_index_specifications;
            DROP TABLE IF EXISTS jira_index_ballots;
            DROP TABLE IF EXISTS jira_index_ballot_targets;
            DROP TABLE IF EXISTS jira_index_ballot_cycles;
            DROP TABLE IF EXISTS jira_index_labels;
            DROP TABLE IF EXISTS jira_index_types;
            DROP TABLE IF EXISTS jira_index_priorities;
            DROP TABLE IF EXISTS jira_index_statuses;
            DROP TABLE IF EXISTS jira_index_resolutions;
            DROP TABLE IF EXISTS jira_index_users;
            DROP TABLE IF EXISTS jira_index_inpersons;
            DROP TABLE IF EXISTS jira_users;
            DROP TABLE IF EXISTS jira_projects;
            DROP TABLE IF EXISTS xref_zulip;
            DROP TABLE IF EXISTS xref_github;
            DROP TABLE IF EXISTS xref_confluence;
            DROP TABLE IF EXISTS xref_fhir_element;
            DROP TABLE IF EXISTS hl7_workgroups;
            """;
        cmd.ExecuteNonQuery();

        InitializeSchema(connection);
    }

}
