using FhirAugury.Processor.Jira.Fhir.Preparer.Hydration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.PreparerSite;

/// <summary>
/// Pre-flights the preparer-site source DB for the post-9f068f5 hydration
/// schema. Detects whether the DB needs hydration (legacy shape OR modern
/// shape with zero <c>prepared_ticket_hydration</c> rows), and either runs
/// hydration in place against the user's DB (with visible progress on
/// stderr) or fails fast under <c>--no-hydrate</c>.
///
/// All diagnostic output (the "Hydration is missing" actionable error and
/// the <c>[info] Hydrating …</c> progress lines) is written to the supplied
/// <c>stderr</c> writer per the slot-08 convention that errors / progress
/// log to stderr.
/// </summary>
internal static class HydrationPreflight
{
    internal sealed record PreflightResult(bool Proceed);

    public static async Task<PreflightResult> RunAsync(
        string dbPath,
        CliOptions options,
        TextWriter stderr,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stderr);

        DbShape shape = await DetectShapeAsync(dbPath, ct).ConfigureAwait(false);

        if (shape == DbShape.NotAPreparerDb)
        {
            return new PreflightResult(Proceed: true);
        }

        if (shape == DbShape.Modern)
        {
            long hydrationRows = await CountHydrationRowsAsync(dbPath, ct).ConfigureAwait(false);
            if (hydrationRows > 0)
            {
                return new PreflightResult(Proceed: true);
            }
        }

        if (options.NoHydrate)
        {
            await stderr.WriteLineAsync(
                $"Hydration is missing for '{dbPath}' (prepared_ticket_hydration is empty or absent).")
                .ConfigureAwait(false);
            await stderr.WriteLineAsync(
                "Re-run without --no-hydrate to populate it, or run FhirAugury.Processor.Jira.Fhir.Preparer to hydrate.")
                .ConfigureAwait(false);
            return new PreflightResult(Proceed: false);
        }

        await RunHydrationAsync(dbPath, options, stderr, ct).ConfigureAwait(false);
        return new PreflightResult(Proceed: true);
    }

    private static async Task RunHydrationAsync(
        string dbPath,
        CliOptions options,
        TextWriter stderr,
        CancellationToken ct)
    {
        string orchestratorUrl = string.IsNullOrWhiteSpace(options.OrchestratorAddress)
            ? HydrationHttpClient.DefaultOrchestratorAddress
            : options.OrchestratorAddress;

        try
        {
            using HttpClient httpClient = HydrationHttpClient.Create(orchestratorUrl);

            PreparerDatabase database = new(dbPath, NullLogger<PreparerDatabase>.Instance);
            try
            {
                database.Initialize();

                List<string> keys = await ListTicketKeysAsync(database, ct).ConfigureAwait(false);
                if (keys.Count == 0)
                {
                    await stderr.WriteLineAsync(
                        "[info] No prepared tickets present; skipping hydration.")
                        .ConfigureAwait(false);
                    return;
                }

                PreparedTicketHydrator hydrator = new(
                    httpClient,
                    database,
                    NullLogger<PreparedTicketHydrator>.Instance);

                await HydrateAllAsync(hydrator, keys, orchestratorUrl, stderr, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                database.Dispose();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static async Task HydrateAllAsync(
        PreparedTicketHydrator hydrator,
        IReadOnlyList<string> keys,
        string orchestratorUrl,
        TextWriter stderr,
        CancellationToken ct)
    {
        int total = keys.Count;
        bool isTty = !Console.IsErrorRedirected;
        TimeSpan minInterval = TimeSpan.FromMilliseconds(250);

        await stderr.WriteLineAsync(
            $"[info] Hydrating {total} prepared tickets via '{orchestratorUrl}'…")
            .ConfigureAwait(false);

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        DateTime lastEmit = DateTime.MinValue;

        for (int i = 0; i < total; i++)
        {
            await hydrator.HydrateAsync(keys[i], ct).ConfigureAwait(false);

            DateTime now = DateTime.UtcNow;
            bool isLast = i == total - 1;
            if (isLast || now - lastEmit >= minInterval)
            {
                lastEmit = now;
                int done = i + 1;
                TimeSpan elapsed = sw.Elapsed;
                TimeSpan eta = done == 0
                    ? TimeSpan.Zero
                    : TimeSpan.FromTicks((elapsed.Ticks / done) * (total - done));
                string line = $"[info] Hydrated {done}/{total} (eta {Format(eta)})";
                if (isTty && !isLast)
                {
                    await stderr.WriteAsync('\r' + line).ConfigureAwait(false);
                }
                else
                {
                    if (isTty)
                    {
                        await stderr.WriteAsync('\r').ConfigureAwait(false);
                    }
                    await stderr.WriteLineAsync(line).ConfigureAwait(false);
                }
            }
        }

        sw.Stop();
        await stderr.WriteLineAsync(
            $"[info] Hydration complete: {total} tickets in {Format(sw.Elapsed)}.")
            .ConfigureAwait(false);
    }

    private static string Format(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero)
        {
            ts = TimeSpan.Zero;
        }
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private static async Task<List<string>> ListTicketKeysAsync(
        PreparerDatabase database,
        CancellationToken ct)
    {
        List<string> keys = [];
        await using SqliteConnection connection = database.OpenConnection();
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Key FROM prepared_tickets ORDER BY Key";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(reader.GetString(0));
        }
        return keys;
    }

    internal enum DbShape
    {
        Modern,
        LegacyMissingHydration,
        NotAPreparerDb,
    }

    private static async Task<DbShape> DetectShapeAsync(string dbPath, CancellationToken ct)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        await using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' " +
            "AND name IN ('prepared_tickets', 'prepared_ticket_hydration')";

        bool hasPreparedTickets = false;
        bool hasHydration = false;
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string name = reader.GetString(0);
            if (name == "prepared_tickets") hasPreparedTickets = true;
            else if (name == "prepared_ticket_hydration") hasHydration = true;
        }

        if (hasHydration) return DbShape.Modern;
        if (hasPreparedTickets) return DbShape.LegacyMissingHydration;
        return DbShape.NotAPreparerDb;
    }

    private static async Task<long> CountHydrationRowsAsync(string dbPath, CancellationToken ct)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        await using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM prepared_ticket_hydration";
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long l ? l : Convert.ToInt64(result);
    }
}
