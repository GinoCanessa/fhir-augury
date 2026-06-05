using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketSite;

/// <summary>
/// Final pass in the trimmed-DB build pipeline. Creates the two normalized
/// child tables that drive the SPA's "By artifact" and "By page" crosscut
/// columns — <c>prepared_ticket_artifacts</c> and
/// <c>prepared_ticket_pages</c> — and (when a <c>--jira-source-db</c> is
/// provided) populates them by joining surviving ticket keys against
/// <c>jira_issues.RelatedArtifacts</c> and
/// <c>jira_baldef.RelatedArtifacts</c> / <c>jira_baldef.RelatedPages</c>
/// from the upstream Jira source DB. Values are comma-split, trimmed, and
/// case-insensitively de-duplicated per ticket, matching the
/// <c>index-planned</c> skill's first-seen-spelling normalization rule.
///
/// VACUUM is performed here as the last step (it cannot run inside a
/// transaction, and this is the last pre-emit mutation), so the bytes the
/// caller reads next are already compact.
///
/// Mirrors <see cref="SpecificationBackfill"/>'s batched <c>IN (...)</c>
/// shape for upstream reads.
/// </summary>
internal static class RelatedFieldsBackfill
{
    private const int SqliteInBatchSize = 500;

    public static async Task ApplyAsync(
        string tempDbPath,
        string? jiraSourceDbPath,
        TextWriter stderr,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(tempDbPath);
        ArgumentNullException.ThrowIfNull(stderr);

        // Schema creation + key load + (optional) upstream backfill run on
        // the same write connection so the indexes are visible to the
        // crosscut SQL the SPA will issue against the inlined DB.
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = tempDbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false, // one-shot schema/backfill + VACUUM; downstream
                             // File.ReadAllBytes must see no pooled native
                             // handle on tempDbPath.
        };

        await using (SqliteConnection connection = new(builder.ConnectionString))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await EnsureSchemaAsync(connection, ct).ConfigureAwait(false);

            bool haveSource =
                !string.IsNullOrEmpty(jiraSourceDbPath) && File.Exists(jiraSourceDbPath);

            if (!haveSource)
            {
                await stderr.WriteLineAsync(
                    "Related-artifact/page backfill skipped (no --jira-source-db).")
                    .ConfigureAwait(false);
            }
            else
            {
                List<string> keys = await SelectSurvivingKeysAsync(connection, ct).ConfigureAwait(false);
                if (keys.Count > 0)
                {
                    await PopulateAsync(connection, jiraSourceDbPath!, keys, stderr, ct)
                        .ConfigureAwait(false);
                }
            }
        }

        // VACUUM has to run outside any transaction and outside the
        // backfill connection's open pool. Reopen + run + close pools so
        // the caller's File.ReadAllBytes sees a coherent file with no
        // .db-wal / .db-shm sidecars.
        await using (SqliteConnection connection = new(builder.ConnectionString))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = "VACUUM";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS prepared_ticket_artifacts (
              TicketKey TEXT NOT NULL,
              Value     TEXT NOT NULL,
              PRIMARY KEY (TicketKey, Value)
            );
            CREATE INDEX IF NOT EXISTS IDX_prepared_ticket_artifacts_Value
              ON prepared_ticket_artifacts (Value);
            CREATE TABLE IF NOT EXISTS prepared_ticket_pages (
              TicketKey TEXT NOT NULL,
              Value     TEXT NOT NULL,
              PRIMARY KEY (TicketKey, Value)
            );
            CREATE INDEX IF NOT EXISTS IDX_prepared_ticket_pages_Value
              ON prepared_ticket_pages (Value);
            """;
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<List<string>> SelectSurvivingKeysAsync(
        SqliteConnection connection, CancellationToken ct)
    {
        List<string> keys = [];
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Key FROM prepared_tickets";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(reader.GetString(0));
        }
        return keys;
    }

    private static async Task PopulateAsync(
        SqliteConnection writeConnection,
        string jiraSourceDbPath,
        IReadOnlyList<string> keys,
        TextWriter stderr,
        CancellationToken ct)
    {
        // Per-ticket accumulators: case-insensitive de-dup with first-seen
        // spelling preserved. (artifact, page) populated independently so
        // a row in only one upstream table still surfaces its values.
        Dictionary<string, OrderedCaseInsensitiveSet> artifactsByKey = new(StringComparer.Ordinal);
        Dictionary<string, OrderedCaseInsensitiveSet> pagesByKey = new(StringComparer.Ordinal);

        SqliteConnectionStringBuilder sourceBuilder = new()
        {
            DataSource = jiraSourceDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false, // one-shot per PopulateAsync call; preserves the
                             // pre-edit behaviour of the now-removed
                             // SqliteConnection.ClearAllPools() that also
                             // cleared this source pool.
        };
        await using SqliteConnection sourceConn = new(sourceBuilder.ConnectionString);
        await sourceConn.OpenAsync(ct).ConfigureAwait(false);

        bool issuesQueried = false;
        bool baldefQueried = false;

        for (int start = 0; start < keys.Count; start += SqliteInBatchSize)
        {
            int end = Math.Min(start + SqliteInBatchSize, keys.Count);

            // jira_issues.RelatedArtifacts (FHIR change-request tickets).
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
                    $"SELECT Key, RelatedArtifacts FROM jira_issues " +
                    $"WHERE Key IN ({string.Join(", ", placeholders)})";
                await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    string key = reader.GetString(0);
                    string? raw = reader.IsDBNull(1) ? null : reader.GetString(1);
                    foreach (string v in NormalizeAndSplit(raw))
                    {
                        AddTo(artifactsByKey, key, v);
                    }
                }
            }
            catch (SqliteException ex)
            {
                if (!issuesQueried)
                {
                    await stderr.WriteLineAsync(
                        $"Related-artifact backfill skipped jira_issues ({ex.Message}).")
                        .ConfigureAwait(false);
                }
                issuesQueried = true;
                // continue with the next source table
            }

            // jira_baldef.RelatedArtifacts / RelatedPages (ballot-definition tickets).
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
                    $"SELECT Key, RelatedArtifacts, RelatedPages FROM jira_baldef " +
                    $"WHERE Key IN ({string.Join(", ", placeholders)})";
                await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    string key = reader.GetString(0);
                    string? rawArtifacts = reader.IsDBNull(1) ? null : reader.GetString(1);
                    string? rawPages = reader.IsDBNull(2) ? null : reader.GetString(2);
                    foreach (string v in NormalizeAndSplit(rawArtifacts))
                    {
                        AddTo(artifactsByKey, key, v);
                    }
                    foreach (string v in NormalizeAndSplit(rawPages))
                    {
                        AddTo(pagesByKey, key, v);
                    }
                }
            }
            catch (SqliteException ex)
            {
                if (!baldefQueried)
                {
                    await stderr.WriteLineAsync(
                        $"Related-artifact/page backfill skipped jira_baldef ({ex.Message}).")
                        .ConfigureAwait(false);
                }
                baldefQueried = true;
            }
        }

        if (artifactsByKey.Count == 0 && pagesByKey.Count == 0)
        {
            return;
        }

        await using SqliteTransaction tx = (SqliteTransaction)await writeConnection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await InsertAsync(writeConnection, tx, "prepared_ticket_artifacts", artifactsByKey, ct).ConfigureAwait(false);
        await InsertAsync(writeConnection, tx, "prepared_ticket_pages", pagesByKey, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string table,
        Dictionary<string, OrderedCaseInsensitiveSet> byKey,
        CancellationToken ct)
    {
        if (byKey.Count == 0)
        {
            return;
        }

        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT OR IGNORE INTO {table} (TicketKey, Value) VALUES ($k, $v)";
        SqliteParameter pk = cmd.CreateParameter();
        pk.ParameterName = "$k";
        cmd.Parameters.Add(pk);
        SqliteParameter pv = cmd.CreateParameter();
        pv.ParameterName = "$v";
        cmd.Parameters.Add(pv);

        foreach ((string key, OrderedCaseInsensitiveSet values) in byKey)
        {
            foreach (string value in values)
            {
                pk.Value = key;
                pv.Value = value;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private static void AddTo(
        Dictionary<string, OrderedCaseInsensitiveSet> map, string key, string value)
    {
        if (!map.TryGetValue(key, out OrderedCaseInsensitiveSet? set))
        {
            set = new OrderedCaseInsensitiveSet();
            map[key] = set;
        }
        set.Add(value);
    }

    /// <summary>
    /// Comma-splits <paramref name="raw"/>, trims whitespace, drops empties,
    /// and de-duplicates within the value list using
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> while preserving the
    /// first-seen casing. Mirrors the <c>index-planned</c> skill's
    /// "first-seen spelling" rule (SKILL.md, "Related artifact normalization").
    /// </summary>
    internal static IEnumerable<string> NormalizeAndSplit(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            yield break;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        string[] parts = raw.Split(',');
        foreach (string part in parts)
        {
            string trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            if (seen.Add(trimmed))
            {
                yield return trimmed;
            }
        }
    }

    /// <summary>
    /// Insertion-ordered set with case-insensitive equality and first-seen
    /// casing preserved.
    /// </summary>
    private sealed class OrderedCaseInsensitiveSet
    {
        private readonly List<string> _items = [];
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string value)
        {
            if (_seen.Add(value))
            {
                _items.Add(value);
            }
        }

        public List<string>.Enumerator GetEnumerator() => _items.GetEnumerator();
    }
}
