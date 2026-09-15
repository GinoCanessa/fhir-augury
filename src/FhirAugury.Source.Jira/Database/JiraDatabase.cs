using FhirAugury.Common.Database;
using FhirAugury.Common.Database.Records;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Ingestion;
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
        => InitializeSchema(connection, recoverStaleMutation: true);

    private void InitializeSchema(SqliteConnection connection, bool recoverStaleMutation)
    {
        JiraUserRecord.CreateTable(connection);
        JiraProjectRecord.CreateTable(connection);
        SqliteSchemaHelpers.AddColumnIfMissing(
            connection,
            "jira_issues",
            "RelatedPages",
            "TEXT NULL");
        JiraIssueRecord.CreateTable(connection);
        JiraProjectScopeStatementRecord.CreateTable(connection);
        JiraBaldefRecord.CreateTable(connection);
        JiraBallotRecord.CreateTable(connection);
        JiraCommentRecord.CreateTable(connection);
        JiraIssueLinkRecord.CreateTable(connection);
        JiraIssueRelatedRecord.CreateTable(connection);
        JiraSyncStateRecord.CreateTable(connection);
        JiraSourceStateRecord.CreateTable(connection);
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

        MigrateSchema(connection, recoverStaleMutation);
    }

    /// <summary>
    /// Applies schema migrations for tables/columns added after initial
    /// release. Safe to call repeatedly; each migration checks before altering.
    /// </summary>
    private static void MigrateSchema(
        SqliteConnection connection,
        bool recoverStaleMutation)
    {
        // Additive source-provenance migrations must run before any generated
        // reader materializes the newly-added record properties.
        AddColumnIfMissing(
            connection,
            "sync_state",
            "LastSuccessfulSyncAt",
            "TEXT NULL");
        AddColumnIfMissing(
            connection,
            "jira_users",
            "HasExplicitDisplayName",
            "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(
            connection,
            "jira_users",
            "HasAccountUsername",
            "INTEGER NOT NULL DEFAULT 1");
        foreach (string table in new[]
                 {
                     "jira_issues",
                     "jira_pss",
                     "jira_baldef",
                     "jira_ballot",
                 })
        {
            AddColumnIfMissing(
                connection,
                table,
                "AssigneeUserId",
                "INTEGER NULL");
            AddColumnIfMissing(
                connection,
                table,
                "ReporterUserId",
                "INTEGER NULL");
        }

        // A legacy successful upstream row is the only unambiguous source for
        // a historical watermark. Rebuilds, partial/error outcomes, and
        // unknown run types intentionally remain null.
        using (SqliteCommand backfill = connection.CreateCommand())
        {
            backfill.CommandText = """
                UPDATE sync_state
                SET LastSuccessfulSyncAt = LastSyncAt
                WHERE LastSuccessfulSyncAt IS NULL
                  AND lower(trim(SourceName)) = 'jira'
                  AND lower(trim(Status)) = 'success'
                  AND lower(trim(
                        CASE
                          WHEN instr(SubSource, ':') > 0
                            THEN substr(SubSource, instr(SubSource, ':') + 1)
                          ELSE SubSource
                        END
                      )) IN ('full', 'incremental')
                """;
            backfill.ExecuteNonQuery();
        }

        // Migration: add jira_projects table for older databases that did
        // not include it at initial schema creation. CreateTable is
        // idempotent (CREATE TABLE IF NOT EXISTS), so this is safe.
        JiraProjectRecord.CreateTable(connection);
        RealignProjectLastSyncAt(connection);

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

        EnsureSourceState(connection);
        if (recoverStaleMutation)
        {
            RecoverStaleSourceState(connection);
        }
    }

    private static void EnsureSourceState(SqliteConnection connection)
    {
        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO jira_source_state
                (Id, ContentRevision, MutationInProgress, UpdatedAt)
            VALUES
                (@id, 0, 0, @updatedAt)
            """;
        insert.Parameters.AddWithValue("@id", JiraSourceStateRecord.SingletonId);
        insert.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow);
        insert.ExecuteNonQuery();
    }

    private static void RecoverStaleSourceState(SqliteConnection connection)
    {
        JiraSourceStateRecord sourceState = ReadSourceState(connection);
        if (!sourceState.MutationInProgress)
        {
            return;
        }

        using SqliteCommand update = connection.CreateCommand();
        update.CommandText = """
            UPDATE jira_source_state
            SET ContentRevision = ContentRevision + 1,
                MutationInProgress = 0,
                UpdatedAt = @updatedAt
            WHERE Id = @id
            """;
        update.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow);
        update.Parameters.AddWithValue("@id", JiraSourceStateRecord.SingletonId);
        update.ExecuteNonQuery();
    }

    private static void RealignProjectLastSyncAt(SqliteConnection connection)
    {
        IReadOnlyDictionary<string, DateTimeOffset?> watermarks =
            JiraSyncStateHelper.CaptureProjectWatermarks(connection);

        foreach (JiraProjectRecord project in JiraProjectRecord.SelectList(connection))
        {
            DateTimeOffset? canonical = watermarks.TryGetValue(
                project.Key,
                out DateTimeOffset? watermark)
                ? watermark
                : null;
            if (project.LastSyncAt == canonical)
            {
                continue;
            }

            project.LastSyncAt = canonical;
            JiraProjectRecord.Update(connection, project);
        }
    }

    /// <summary>Reads the singleton source generation state.</summary>
    public static JiraSourceStateRecord ReadSourceState(SqliteConnection connection)
        => JiraSourceStateRecord.SelectSingle(
               connection,
               Id: JiraSourceStateRecord.SingletonId)
           ?? throw new InvalidOperationException("The Jira source state row is missing.");

    internal static readonly string[] PublicPeopleShapeTables =
        ["jira_issues", "jira_pss", "jira_baldef", "jira_ballot"];

    internal static JiraPeopleState ReadPublicPeopleState(SqliteConnection connection, CancellationToken ct)
    {
        JiraPeopleSourceBefore source;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT ContentRevision, MutationInProgress, UpdatedAt FROM jira_source_state WHERE Id = @id";
            command.Parameters.AddWithValue("@id", JiraSourceStateRecord.SingletonId);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("The Jira source state row is missing.");
            source = new(reader.GetInt64(0), reader.GetBoolean(1), reader.GetString(2));
        }

        List<JiraPeopleIssueBefore> issues = [];
        foreach (string table in PublicPeopleShapeTables)
        {
            ct.ThrowIfCancellationRequested();
            using SqliteCommand command = connection.CreateCommand();
            string votes = table == "jira_issues" ? "VoteMover, VoteSeconder" : "NULL, NULL";
            command.CommandText = $"""
                SELECT Id, Key, UpdatedAt, ReporterUserId, AssigneeUserId, Reporter, Assignee, {votes}
                FROM {table} ORDER BY Key COLLATE BINARY, Id
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                issues.Add(new(table, reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }
        List<JiraPeopleRequesterBefore> requesters = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, IssueKey, UserId FROM jira_issue_inpersons ORDER BY Id";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                requesters.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
            }
        }
        return new(source, issues, JiraUserRecord.SelectList(connection), requesters);
    }

    /// <summary>
    /// The caller holds the pipeline gate. Revalidation, allowlisted people
    /// writes, both lookups and the single generation increment share this
    /// connection and writer transaction; no ingestion marker is ever cleared.
    /// </summary>
    internal JiraPublicPeopleApplyResponse ApplyPublicPeople(
        JiraPeoplePlan preview, JiraIndexBuilder indexBuilder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand transaction = connection.CreateCommand();
        transaction.CommandText = "BEGIN IMMEDIATE;";
        transaction.ExecuteNonQuery();
        bool committed = false;
        try
        {
            JiraPeopleState current = ReadPublicPeopleState(connection, ct);
            string? refusal = current.Source.MutationInProgress ? JiraPublicPeopleCodes.SourceBusy
                : current.Source.ContentRevision != preview.Source.ContentRevision ? JiraPublicPeopleCodes.StalePreview : null;
            JiraPeoplePlan? verified = null;
            if (refusal is null)
            {
                verified = JiraPeoplePlanner.Build(current, preview.Keys, preview.Evidence, ct);
                refusal = verified.ImpactFingerprint != preview.ImpactFingerprint ? JiraPublicPeopleCodes.SharedImpactChanged
                    : verified.Fingerprint != preview.Fingerprint ? JiraPublicPeopleCodes.StalePreview
                    : verified.Response.Code != JiraPublicPeopleCodes.PreviewReady ? verified.Response.Code : null;
            }
            if (refusal is not null)
                return Rollback(refusal);

            JiraPeoplePlan plan = verified ?? throw new InvalidOperationException("People verification is required.");
            Dictionary<string, int> userIds = current.Users.ToDictionary(user => user.Username, user => user.Id, StringComparer.Ordinal);
            foreach (JiraPeopleUserChange change in plan.Users)
            {
                ct.ThrowIfCancellationRequested();
                using SqliteCommand command = connection.CreateCommand();
                command.Parameters.AddWithValue("@identity", change.Identity);
                command.Parameters.AddWithValue("@name", change.DisplayName);
                if (change.Before is JiraUserRecord before)
                {
                    command.CommandText = """
                        UPDATE jira_users SET DisplayName = @name, HasExplicitDisplayName = 1
                        WHERE Id = @id AND Username = @identity COLLATE BINARY
                          AND DisplayName = @beforeName COLLATE BINARY
                          AND HasAccountUsername = 1 AND HasExplicitDisplayName = @beforeExplicit
                        """;
                    command.Parameters.AddWithValue("@id", before.Id);
                    command.Parameters.AddWithValue("@beforeName", before.DisplayName);
                    command.Parameters.AddWithValue("@beforeExplicit", before.HasExplicitDisplayName);
                }
                else
                {
                    int id = JiraUserRecord.GetIndex();
                    command.CommandText = """
                        INSERT INTO jira_users (Id, Username, DisplayName, HasAccountUsername, HasExplicitDisplayName)
                        SELECT @id, @identity, @name, 1, 1
                        WHERE NOT EXISTS (SELECT 1 FROM jira_users WHERE Username = @identity COLLATE BINARY)
                        """;
                    command.Parameters.AddWithValue("@id", id);
                    userIds.Add(change.Identity, id);
                }
                if (command.ExecuteNonQuery() != 1)
                    return Rollback(JiraPublicPeopleCodes.WriteFailed);
            }

            foreach (JiraPeopleIssueChange change in plan.Issues)
            {
                ct.ThrowIfCancellationRequested();
                JiraPeopleIssueBefore before = change.Before;
                if (!PublicPeopleShapeTables.Contains(before.Table, StringComparer.Ordinal)
                    || (change.ReporterIdentity is not null && before.ReporterUserId is not null)
                    || (change.AssigneeIdentity is not null && before.AssigneeUserId is not null))
                    throw new InvalidOperationException("Only missing people bindings may be populated.");
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = $"""
                    UPDATE {before.Table} SET ReporterUserId = @reporter, AssigneeUserId = @assignee
                    WHERE Id = @id AND Key = @key COLLATE BINARY AND UpdatedAt = @revision COLLATE BINARY
                      AND ReporterUserId IS @beforeReporter AND AssigneeUserId IS @beforeAssignee
                    """;
                command.Parameters.AddWithValue("@reporter", (object?)(change.ReporterIdentity is null
                    ? before.ReporterUserId : userIds[change.ReporterIdentity]) ?? DBNull.Value);
                command.Parameters.AddWithValue("@assignee", (object?)(change.AssigneeIdentity is null
                    ? before.AssigneeUserId : userIds[change.AssigneeIdentity]) ?? DBNull.Value);
                command.Parameters.AddWithValue("@id", before.Id);
                command.Parameters.AddWithValue("@key", before.Key);
                command.Parameters.AddWithValue("@revision", before.UpdatedAt);
                command.Parameters.AddWithValue("@beforeReporter", (object?)before.ReporterUserId ?? DBNull.Value);
                command.Parameters.AddWithValue("@beforeAssignee", (object?)before.AssigneeUserId ?? DBNull.Value);
                if (command.ExecuteNonQuery() != 1)
                    return Rollback(JiraPublicPeopleCodes.WriteFailed);
            }
            foreach (JiraPeopleRequesterAddition addition in plan.Requesters)
            {
                ct.ThrowIfCancellationRequested();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO jira_issue_inpersons (Id, IssueKey, UserId)
                    SELECT @id, @key, @user
                    WHERE NOT EXISTS (
                        SELECT 1 FROM jira_issue_inpersons WHERE IssueKey = @key COLLATE BINARY AND UserId = @user)
                    """;
                command.Parameters.AddWithValue("@id", JiraIssueInPersonRecord.GetIndex());
                command.Parameters.AddWithValue("@key", addition.Key);
                command.Parameters.AddWithValue("@user", userIds[addition.Identity]);
                if (command.ExecuteNonQuery() != 1)
                    return Rollback(JiraPublicPeopleCodes.WriteFailed);
            }

            JiraPublicPeopleChanges changes = plan.Response.Changes;
            if (changes.HasChanges)
            {
                indexBuilder.RebuildPeopleIndexes(connection);
                ct.ThrowIfCancellationRequested();
                using SqliteCommand update = connection.CreateCommand();
                update.CommandText = """
                    UPDATE jira_source_state
                    SET ContentRevision = ContentRevision + 1, UpdatedAt = @updatedAt
                    WHERE Id = @id AND ContentRevision = @revision
                      AND MutationInProgress = 0 AND UpdatedAt = @beforeUpdatedAt COLLATE BINARY
                    """;
                update.Parameters.AddWithValue("@id", JiraSourceStateRecord.SingletonId);
                update.Parameters.AddWithValue("@revision", preview.Source.ContentRevision);
                update.Parameters.AddWithValue("@beforeUpdatedAt", preview.Source.UpdatedAt);
                update.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow);
                if (update.ExecuteNonQuery() != 1)
                    return Rollback(JiraPublicPeopleCodes.WriteFailed);
            }
            ct.ThrowIfCancellationRequested();
            transaction.CommandText = "COMMIT;";
            transaction.ExecuteNonQuery();
            committed = true;
            // Do not observe request cancellation after the known commit.
            return new(changes.HasChanges ? JiraPublicPeopleCodes.Applied : JiraPublicPeopleCodes.NoChange,
                preview.Source.ContentRevision + (changes.HasChanges ? 1 : 0), changes);
        }
        catch
        {
            if (!committed)
            {
                transaction.CommandText = "ROLLBACK;";
                transaction.ExecuteNonQuery();
            }
            throw;
        }

        JiraPublicPeopleApplyResponse Rollback(string code)
        {
            transaction.CommandText = "ROLLBACK;";
            transaction.ExecuteNonQuery();
            return new(code);
        }
    }

    /// <summary>
    /// Advances the source revision and marks the following writes unstable.
    /// </summary>
    public JiraSourceStateRecord BeginContentMutation(CancellationToken ct = default)
        => AdvanceContentRevision(mutationInProgress: true, ct);

    /// <summary>
    /// Advances the source revision once more and exposes the resulting
    /// on-disk generation as stable, including after a failed mutation.
    /// </summary>
    public JiraSourceStateRecord CompleteContentMutation(CancellationToken ct = default)
        => AdvanceContentRevision(mutationInProgress: false, ct);

    private JiraSourceStateRecord AdvanceContentRevision(
        bool mutationInProgress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand update = connection.CreateCommand();
        update.CommandText = """
            UPDATE jira_source_state
            SET ContentRevision = ContentRevision + 1,
                MutationInProgress = @mutationInProgress,
                UpdatedAt = @updatedAt
            WHERE Id = @id
            """;
        update.Parameters.AddWithValue("@mutationInProgress", mutationInProgress);
        update.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow);
        update.Parameters.AddWithValue("@id", JiraSourceStateRecord.SingletonId);
        if (update.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("The Jira source state row is missing.");
        }

        return ReadSourceState(connection);
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

        IReadOnlyDictionary<string, DateTimeOffset?> watermarks =
            JiraSyncStateHelper.CaptureProjectWatermarks(connection);
        JiraSourceStateRecord sourceState = ReadSourceState(connection);

        ResetDatabase(
            connection,
            watermarks,
            sourceState.ContentRevision,
            ct);

        // A direct reset owns its complete mutation fence. Rebuild callers
        // begin the outer fence first and complete it after replay/indexing.
        if (!sourceState.MutationInProgress)
        {
            CompleteContentMutation(CancellationToken.None);
        }
    }

    /// <summary>
    /// Resets local storage while preserving previously proven upstream
    /// watermarks. The newly-created database remains mutation-fenced for the
    /// caller's subsequent cache replay.
    /// </summary>
    internal void ResetDatabase(
        IReadOnlyDictionary<string, DateTimeOffset?> watermarks,
        long previousContentRevision,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();
        ResetDatabase(connection, watermarks, previousContentRevision, ct);
    }

    private void ResetDatabase(
        SqliteConnection connection,
        IReadOnlyDictionary<string, DateTimeOffset?> watermarks,
        long previousContentRevision,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using SqliteCommand transactionCommand = connection.CreateCommand();
        transactionCommand.CommandText = "BEGIN IMMEDIATE;";
        transactionCommand.ExecuteNonQuery();

        try
        {
            long resetContentRevision = checked(
                Math.Max(
                    previousContentRevision,
                    ReadSourceState(connection).ContentRevision) + 1);

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
            DROP TABLE IF EXISTS jira_source_state;
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

            InitializeSchema(connection, recoverStaleMutation: false);
            JiraSyncStateHelper.RestoreProjectWatermarks(connection, watermarks);

            using (SqliteCommand updateState = connection.CreateCommand())
            {
                updateState.CommandText = """
                    UPDATE jira_source_state
                    SET ContentRevision = @contentRevision,
                        MutationInProgress = 1,
                        UpdatedAt = @updatedAt
                    WHERE Id = @id
                    """;
                updateState.Parameters.AddWithValue(
                    "@contentRevision",
                    resetContentRevision);
                updateState.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow);
                updateState.Parameters.AddWithValue("@id", JiraSourceStateRecord.SingletonId);
                updateState.ExecuteNonQuery();
            }

            transactionCommand.CommandText = "COMMIT;";
            transactionCommand.ExecuteNonQuery();
        }
        catch
        {
            transactionCommand.CommandText = "ROLLBACK;";
            transactionCommand.ExecuteNonQuery();
            throw;
        }
    }

}
