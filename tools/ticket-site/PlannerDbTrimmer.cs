using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
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

    public sealed record BuildResult(string TempDbPath, long SurvivingTicketCount);

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
        CancellationToken ct)
    {
        string tempPath = Path.GetTempFileName();
        try
        {
            // Checkpoint the source DB's WAL before copying so the copy
            // includes any rows still living in -wal / -shm sidecars from
            // unflushed write connections. Without this, a recently-seeded
            // test DB can yield an empty trimmed copy.
            await CheckpointAsync(sourceDbPath, ct).ConfigureAwait(false);
            File.Copy(sourceDbPath, tempPath, overwrite: true);

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
                PlannerDatabase.EnsureSchema(connection);

                await using SqliteTransaction tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

                // Trim planned_tickets by intersection of all active filters.
                // Specification comes from planned_jira_hydration self-rows
                // (JiraKey = IssueKey = pt.Key) — same source the SPA uses.
                await using (SqliteCommand cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
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

                // Topic members keyed by TicketKey.
                await using (SqliteCommand cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM planned_ticket_topic_members WHERE TicketKey NOT IN (SELECT Key FROM planned_tickets)";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // Orphan topic-groups, then orphan topics, then orphan topic-repos.
                await using (SqliteCommand cmd = connection.CreateCommand())
                {
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

                await using (SqliteCommand cmd = connection.CreateCommand())
                {
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

            return new BuildResult(tempPath, surviving);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

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
}
