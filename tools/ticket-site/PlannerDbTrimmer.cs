using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketSite;

internal static class PlannerDbTrimmer
{
    private static readonly string[] ChildTablesByIssueKey =
    [
        "planned_ticket_repos",
        "planned_ticket_repo_changes",
        "planned_ticket_repo_impacts",
        "planned_ticket_change_validations",
        "planned_ticket_testing_considerations",
        "planned_ticket_open_questions",
        "planned_ticket_related_jira",
        "planned_ticket_related_zulip",
        "planned_ticket_related_github",
        "planned_ticket_jira_xref",
        "planned_ticket_hydration",
        "planned_jira_hydration",
        "planned_zulip_hydration",
        "planned_github_hydration",
        "planned_repo_hydration",
    ];

    public sealed record BuildResult(
        string TempDbPath,
        long SurvivingTicketCount,
        bool OwnsTempFile);

    /// <summary>
    /// Copies the source planner DB to a temp file, self-migrates older DBs
    /// via <see cref="PlannerDatabase.EnsureSchema"/>, and runs the
    /// filter-aware trim. Mirrors <c>PreparerDbTrimmer.BuildAsync</c> in
    /// structure; orphan topic / topic-group / topic-member rows are dropped
    /// after the per-issue child trim.
    /// </summary>
    public static async Task<BuildResult> BuildAsync(
        string sourceDbPath,
        ResolvedFilters filters,
        bool immutableSnapshot,
        CancellationToken ct)
    {
        if (immutableSnapshot && !filters.HasAnyFilter)
        {
            return new BuildResult(
                sourceDbPath,
                await CountAsync(sourceDbPath, ct).ConfigureAwait(false),
                OwnsTempFile: false);
        }

        string tempPath = Path.GetTempFileName();
        try
        {
            // Checkpoint the source DB's WAL before copying so the copy
            // includes any rows still living in -wal / -shm sidecars from
            // unflushed write connections. Without this, a recently-seeded
            // test DB can yield an empty trimmed copy.
            if (!immutableSnapshot)
            {
                await CheckpointAsync(sourceDbPath, ct).ConfigureAwait(false);
            }
            File.Copy(sourceDbPath, tempPath, overwrite: true);
            File.SetAttributes(tempPath, FileAttributes.Normal);

            long surviving;
            SqliteConnectionStringBuilder builder = new()
            {
                DataSource = tempPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false, // one-shot trim; downstream File.ReadAllBytes
                                 // must see no pooled native handle on tempPath.
            };
            await using (SqliteConnection connection = new(builder.ConnectionString))
            {
                await connection.OpenAsync(ct).ConfigureAwait(false);
                if (!immutableSnapshot)
                {
                    FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.PlannerDatabase
                        .EnsureSchema(connection);
                }

                await using SqliteTransaction tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

                // Trim planned_tickets by intersection of all active filters.
                // Specification comes from planned_jira_hydration self-rows
                // (JiraKey = IssueKey = pt.Key) — same source the SPA uses.
                await using (SqliteCommand cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = immutableSnapshot
                        ? """
                          DELETE FROM planned_tickets WHERE Key NOT IN (
                            SELECT pt.Key FROM planned_tickets pt
                            LEFT JOIN planned_jira_hydration jh
                              ON jh.IssueKey = pt.Key AND jh.JiraKey = pt.Key
                            LEFT JOIN planned_ticket_hydration pth ON pth.IssueKey = pt.Key
                            WHERE (@project IS NULL OR LOWER(substr(pt.Key, 1, instr(pt.Key, '-') - 1)) = LOWER(@project))
                              AND (@wg      IS NULL OR LOWER(jh.WorkGroup) = LOWER(@wg))
                              AND (@spec    IS NULL OR LOWER(COALESCE(pth.Specification, jh.Specification)) = LOWER(@spec))
                          )
                          """
                        : """
                          DELETE FROM planned_tickets WHERE Key NOT IN (
                            SELECT pt.Key FROM planned_tickets pt
                            LEFT JOIN jira_processing_source_tickets jst ON jst.Key = pt.Key
                            LEFT JOIN planned_jira_hydration jh ON jh.IssueKey = pt.Key AND jh.JiraKey = pt.Key
                            WHERE (@project IS NULL OR LOWER(jst.Project) = LOWER(@project))
                              AND (@wg      IS NULL OR LOWER(jst.WorkGroup) = LOWER(@wg))
                              AND (@spec    IS NULL OR LOWER(jh.Specification) = LOWER(@spec))
                          )
                          """;
                    cmd.Parameters.AddWithValue("@project", (object?)filters.Project ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@wg", (object?)filters.WorkGroup ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@spec", (object?)filters.Specification ?? DBNull.Value);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                foreach (string table in ChildTablesByIssueKey)
                {
                    await using SqliteCommand cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = $"DELETE FROM {table} WHERE IssueKey NOT IN (SELECT Key FROM planned_tickets)";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (SqliteCommand cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = immutableSnapshot
                        ? "DELETE FROM planned_ticket_jira_content " +
                          "WHERE TicketKey NOT IN (SELECT Key FROM planned_tickets)"
                        : "DELETE FROM planned_ticket_jira_content";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // Topic members keyed by TicketKey.
                await using (SqliteCommand cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM planned_ticket_topic_members WHERE TicketKey NOT IN (SELECT Key FROM planned_tickets)";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                if (immutableSnapshot)
                {
                    await using SqliteCommand cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        """
                        CREATE TEMP TABLE invalid_filtered_groups AS
                        SELECT g.RowId
                        FROM planned_ticket_topic_groups g
                        LEFT JOIN planned_ticket_topic_members m
                          ON m.TopicGroupRowId = g.RowId
                        GROUP BY g.RowId, g.FirstTicketKey
                        HAVING COUNT(m.TicketKey) < 2
                           OR SUM(CASE WHEN m.TicketKey = g.FirstTicketKey THEN 1 ELSE 0 END) = 0;
                        UPDATE planned_ticket_topic_members
                        SET TopicGroupRowId = NULL
                        WHERE TopicGroupRowId IN (SELECT RowId FROM invalid_filtered_groups);
                        DELETE FROM planned_ticket_topic_groups
                        WHERE RowId IN (SELECT RowId FROM invalid_filtered_groups);
                        """;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    await using SqliteCommand cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "DELETE FROM planned_ticket_topic_groups WHERE RowId NOT IN (" +
                        "SELECT DISTINCT TopicGroupRowId FROM planned_ticket_topic_members " +
                        "WHERE TopicGroupRowId IS NOT NULL)";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (SqliteCommand cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "DELETE FROM planned_ticket_topics WHERE RowId NOT IN (" +
                        "SELECT DISTINCT TopicRowId FROM planned_ticket_topic_members)";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (SqliteCommand cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "DELETE FROM planned_ticket_topic_repos WHERE TopicRowId NOT IN (SELECT RowId FROM planned_ticket_topics)";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                if (!immutableSnapshot)
                {
                    await using SqliteCommand cmd = connection.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "DELETE FROM jira_processing_source_tickets " +
                        "WHERE Key NOT IN (SELECT Key FROM planned_tickets)";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (SqliteCommand cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT COUNT(*) FROM planned_tickets";
                    object? value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    surviving = value is long l ? l : Convert.ToInt64(value);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);

                await using SqliteCommand vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM";
                await vacuum.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return new BuildResult(tempPath, surviving, OwnsTempFile: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public static Task<BuildResult> BuildAsync(
        string sourceDbPath,
        ResolvedFilters filters,
        CancellationToken ct)
        => BuildAsync(sourceDbPath, filters, immutableSnapshot: false, ct);

    private static async Task CheckpointAsync(string sourceDbPath, CancellationToken ct)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = sourceDbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        await using SqliteConnection conn = new(builder.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(FULL);";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long> CountAsync(
        string dbPath,
        CancellationToken ct)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM planned_tickets";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }
}
