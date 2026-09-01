using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketSite;

/// <summary>
/// Inlines a per-ticket <c>&lt;prefix&gt;_ticket_jira_content</c> table carrying
/// the authored HTML <c>Description</c> / <c>ResolutionDescription</c> pulled
/// from the upstream Jira source DB's <c>jira_issues</c> table. The HTML only
/// lives in <c>jira.db</c>, so this backfill runs at emit time when a
/// <c>--jira-source-db</c> is supplied; otherwise it leaves an empty (but
/// present) table so the SPA's defensive query never throws.
///
/// Peer to <see cref="RelatedFieldsBackfill"/> but parametrized so the same
/// logic serves both the preparer (discussion) and planner (applying) emit
/// flows. On the preparer path <see cref="RelatedFieldsBackfill"/> runs its
/// finalizing VACUUM afterward, so this call passes
/// <paramref name="finalizeWithVacuum"/> = <see langword="false"/>. On the
/// planner path no other backfill follows, so this call owns the final
/// VACUUM/checkpoint (<paramref name="finalizeWithVacuum"/> =
/// <see langword="true"/>) and guarantees the new table/rows are written into
/// the main DB file with no leftover <c>-wal</c>/<c>-shm</c> sidecars before
/// the caller's <c>File.ReadAllBytes</c>.
/// </summary>
internal static class JiraContentBackfill
{
    private const int SqliteInBatchSize = 500;

    public static async Task ApplyAsync(
        string tempDbPath,
        string ticketsTable,
        string contentTable,
        string? jiraSourceDbPath,
        bool finalizeWithVacuum,
        TextWriter stderr,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(tempDbPath);
        ArgumentException.ThrowIfNullOrEmpty(ticketsTable);
        ArgumentException.ThrowIfNullOrEmpty(contentTable);
        ArgumentNullException.ThrowIfNull(stderr);

        // Schema creation + key load + (optional) upstream backfill run on the
        // same write connection. Pooling=false so downstream File.ReadAllBytes
        // sees no pooled native handle on tempDbPath (mirrors
        // RelatedFieldsBackfill).
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = tempDbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };

        await using (SqliteConnection connection = new(builder.ConnectionString))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await EnsureSchemaAsync(connection, contentTable, ct).ConfigureAwait(false);

            bool haveSource =
                !string.IsNullOrEmpty(jiraSourceDbPath) && File.Exists(jiraSourceDbPath);

            if (!haveSource)
            {
                await stderr.WriteLineAsync(
                    "Jira request/resolution backfill skipped (no --jira-source-db).")
                    .ConfigureAwait(false);
            }
            else
            {
                List<string> keys =
                    await SelectSurvivingKeysAsync(connection, ticketsTable, ct).ConfigureAwait(false);
                if (keys.Count > 0)
                {
                    await PopulateAsync(connection, contentTable, jiraSourceDbPath!, keys, stderr, ct)
                        .ConfigureAwait(false);
                }
            }
        }

        // On the planner path this is the last inline-DB mutation, so it owns
        // the finalizing VACUUM (runs outside any transaction / open pool) to
        // checkpoint the new table/rows into the main DB file before the
        // caller's File.ReadAllBytes — no leftover .db-wal / .db-shm sidecars.
        if (finalizeWithVacuum)
        {
            await using SqliteConnection connection = new(builder.ConnectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "VACUUM";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task EnsureSchemaAsync(
        SqliteConnection connection, string contentTable, CancellationToken ct)
    {
        // contentTable is a fixed internal constant per call site (never user
        // input) — safe to interpolate.
        string ddl = $"""
            CREATE TABLE IF NOT EXISTS {contentTable} (
              TicketKey                 TEXT NOT NULL PRIMARY KEY,
              DescriptionHtml           TEXT,
              ResolutionDescriptionHtml TEXT
            );
            """;
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<List<string>> SelectSurvivingKeysAsync(
        SqliteConnection connection, string ticketsTable, CancellationToken ct)
    {
        List<string> keys = [];
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT Key FROM {ticketsTable}";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(reader.GetString(0));
        }
        return keys;
    }

    private static async Task PopulateAsync(
        SqliteConnection writeConnection,
        string contentTable,
        string jiraSourceDbPath,
        IReadOnlyList<string> keys,
        TextWriter stderr,
        CancellationToken ct)
    {
        Dictionary<string, (string? Description, string? Resolution)> contentByKey =
            new(StringComparer.Ordinal);

        SqliteConnectionStringBuilder sourceBuilder = new()
        {
            DataSource = jiraSourceDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        await using SqliteConnection sourceConn = new(sourceBuilder.ConnectionString);
        await sourceConn.OpenAsync(ct).ConfigureAwait(false);

        bool issuesQueried = false;

        for (int start = 0; start < keys.Count; start += SqliteInBatchSize)
        {
            int end = Math.Min(start + SqliteInBatchSize, keys.Count);

            try
            {
                await using SqliteCommand cmd = sourceConn.CreateCommand();
                List<string> placeholders = [];
                for (int i = start; i < end; i++)
                {
                    string param = $"@k{i}";
                    placeholders.Add(param);
                    cmd.Parameters.AddWithValue(param, keys[i]);
                }
                cmd.CommandText =
                    $"SELECT Key, Description, ResolutionDescription FROM jira_issues " +
                    $"WHERE Key IN ({string.Join(", ", placeholders)})";
                await using SqliteDataReader reader =
                    await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    string key = reader.GetString(0);
                    string? description = reader.IsDBNull(1) ? null : reader.GetString(1);
                    string? resolution = reader.IsDBNull(2) ? null : reader.GetString(2);
                    contentByKey[key] = (description, resolution);
                }
            }
            catch (SqliteException ex)
            {
                if (!issuesQueried)
                {
                    await stderr.WriteLineAsync(
                        $"Jira request/resolution backfill skipped jira_issues ({ex.Message}).")
                        .ConfigureAwait(false);
                }
                issuesQueried = true;
                // No source table to read — stop scanning further batches.
                return;
            }
        }

        if (contentByKey.Count == 0)
        {
            return;
        }

        await using SqliteTransaction tx =
            (SqliteTransaction)await writeConnection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using SqliteCommand insert = writeConnection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            $"INSERT OR IGNORE INTO {contentTable} " +
            $"(TicketKey, DescriptionHtml, ResolutionDescriptionHtml) VALUES ($k, $d, $r)";
        SqliteParameter pk = insert.CreateParameter();
        pk.ParameterName = "$k";
        insert.Parameters.Add(pk);
        SqliteParameter pd = insert.CreateParameter();
        pd.ParameterName = "$d";
        insert.Parameters.Add(pd);
        SqliteParameter pr = insert.CreateParameter();
        pr.ParameterName = "$r";
        insert.Parameters.Add(pr);

        foreach ((string key, (string? description, string? resolution)) in contentByKey)
        {
            // Keep the table sparse: only insert a row when at least one of the
            // two HTML values is present.
            bool hasDescription = !string.IsNullOrEmpty(description);
            bool hasResolution = !string.IsNullOrEmpty(resolution);
            if (!hasDescription && !hasResolution)
            {
                continue;
            }

            pk.Value = key;
            pd.Value = hasDescription ? description! : DBNull.Value;
            pr.Value = hasResolution ? resolution! : DBNull.Value;
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }
}
