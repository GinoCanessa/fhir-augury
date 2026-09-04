using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketSite;

internal static class PreparerDbTrimmer
{
    private static readonly string[] ChildTablesByTicketKey =
    [
        "prepared_ticket_related_jira",
        "prepared_ticket_related_github",
        "prepared_ticket_related_zulip",
        "prepared_ticket_jira_xref",
        "prepared_ticket_hydration",
        "prepared_jira_hydration",
        "prepared_zulip_hydration",
        "prepared_github_hydration",
        "prepared_repo_hydration",
        "prepared_ticket_repos",
        "prepared_ticket_topic_members",
        "prepared_ticket_jira_content",
        "prepared_ticket_artifacts",
        "prepared_ticket_pages",
    ];

    public sealed record BuildResult(
        string TempDbPath,
        long SurvivingTicketCount,
        bool OwnsTempFile);

    /// <summary>
    /// Copies the source preparer DB to a temp file and runs the filter-aware
    /// trim (which is a no-op when all <paramref name="filters"/> are
    /// inactive — the WHERE predicates collapse to TRUE). Returns the path
    /// to the temp DB and the surviving ticket count. The temp DB is left
    /// in place so downstream passes (related-fields backfill) can append
    /// to it; the caller owns the temp file and must delete it.
    /// VACUUM is NOT run here — see <c>RelatedFieldsBackfill.ApplyAsync</c>,
    /// which runs as the final pass before bytes are read.
    /// As part of the same transaction, orphan rows in
    /// <c>prepared_ticket_topic_groups</c> and <c>prepared_ticket_topics</c>
    /// (i.e., rows whose every member ticket was trimmed) are removed
    /// after the per-ticket child-table trim so the inlined DB never
    /// ships empty topics.
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
                await CountAsync(sourceDbPath, "prepared_tickets", ct).ConfigureAwait(false),
                OwnsTempFile: false);
        }

        string tempPath = Path.GetTempFileName();
        try
        {
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
            await using SqliteConnection connection = new(builder.ConnectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            if (!immutableSnapshot)
            {
                FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.PreparerDatabase
                    .EnsureSchema(connection);
            }

            await using SqliteTransaction tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Trim prepared_tickets to the intersection of all active filters.
            // Bind each filter as NULL when inactive so its predicate collapses to TRUE.
            await using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = immutableSnapshot
                    ? """
                      DELETE FROM prepared_tickets WHERE Key NOT IN (
                        SELECT pt.Key FROM prepared_tickets pt
                        LEFT JOIN prepared_jira_hydration jh
                          ON jh.TicketKey = pt.Key AND jh.JiraKey = pt.Key
                        LEFT JOIN prepared_ticket_hydration pth ON pth.TicketKey = pt.Key
                        WHERE (@project IS NULL OR LOWER(substr(pt.Key, 1, instr(pt.Key, '-') - 1)) = LOWER(@project))
                          AND (@wg      IS NULL OR LOWER(jh.WorkGroup) = LOWER(@wg))
                          AND (@spec    IS NULL OR LOWER(pth.Specification) = LOWER(@spec))
                      )
                      """
                    : """
                      DELETE FROM prepared_tickets WHERE Key NOT IN (
                        SELECT pt.Key FROM prepared_tickets pt
                        LEFT JOIN jira_processing_source_tickets jst ON jst.Key = pt.Key
                        LEFT JOIN prepared_ticket_hydration pth ON pth.TicketKey = pt.Key
                        WHERE (@project IS NULL OR LOWER(jst.Project) = LOWER(@project))
                          AND (@wg      IS NULL OR LOWER(jst.WorkGroup) = LOWER(@wg))
                          AND (@spec    IS NULL OR LOWER(pth.Specification) = LOWER(@spec))
                      )
                      """;
                cmd.Parameters.AddWithValue("@project", (object?)filters.Project ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@wg", (object?)filters.WorkGroup ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@spec", (object?)filters.Specification ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            foreach (string table in ChildTablesByTicketKey)
            {
                await using SqliteCommand cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    $"DELETE FROM {table} WHERE TicketKey NOT IN (SELECT Key FROM prepared_tickets)";
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
                    FROM prepared_ticket_topic_groups g
                    LEFT JOIN prepared_ticket_topic_members m
                      ON m.TopicGroupRowId = g.RowId
                    GROUP BY g.RowId, g.FirstTicketKey
                    HAVING COUNT(m.TicketKey) < 2
                       OR SUM(CASE WHEN m.TicketKey = g.FirstTicketKey THEN 1 ELSE 0 END) = 0;
                    UPDATE prepared_ticket_topic_members
                    SET TopicGroupRowId = NULL
                    WHERE TopicGroupRowId IN (SELECT RowId FROM invalid_filtered_groups);
                    DELETE FROM prepared_ticket_topic_groups
                    WHERE RowId IN (SELECT RowId FROM invalid_filtered_groups);
                    DELETE FROM prepared_ticket_topic_members
                    WHERE TopicRowId IN (
                      SELECT t.RowId
                      FROM prepared_ticket_topics t
                      LEFT JOIN prepared_ticket_topic_members m ON m.TopicRowId = t.RowId
                      GROUP BY t.RowId
                      HAVING COUNT(m.TicketKey) < 2
                    );
                    """;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await using SqliteCommand cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    "DELETE FROM prepared_ticket_topic_groups WHERE RowId NOT IN (" +
                    "SELECT DISTINCT TopicGroupRowId FROM prepared_ticket_topic_members " +
                    "WHERE TopicGroupRowId IS NOT NULL)";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "DELETE FROM prepared_ticket_topics WHERE RowId NOT IN (" +
                    "SELECT DISTINCT TopicRowId FROM prepared_ticket_topic_members)";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (!immutableSnapshot)
            {
                await using SqliteCommand cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    "DELETE FROM jira_processing_source_tickets " +
                    "WHERE Key NOT IN (SELECT Key FROM prepared_tickets)";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COUNT(*) FROM prepared_tickets";
                object? value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                surviving = value is long l ? l : Convert.ToInt64(value);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);

            if (immutableSnapshot)
            {
                await using SqliteCommand vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM";
                await vacuum.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return new BuildResult(tempPath, surviving, OwnsTempFile: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best-effort */ }
            throw;
        }
    }

    public static Task<BuildResult> BuildAsync(
        string sourceDbPath,
        ResolvedFilters filters,
        CancellationToken ct)
        => BuildAsync(sourceDbPath, filters, immutableSnapshot: false, ct);

    private static async Task<long> CountAsync(
        string dbPath,
        string table,
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
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }
}
