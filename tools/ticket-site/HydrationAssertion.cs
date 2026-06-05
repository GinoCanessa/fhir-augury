using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketSite;

/// <summary>
/// Fail-fast assertion that the preparer DB has already been hydrated.
/// <c>ticket-site</c> discussion sub-site is a pure consumer of a hydrated DB now; the
/// preparer service owns the hydration sweep (startup + admin
/// endpoint). If the DB does not carry a <c>prepared_ticket_hydration</c>
/// table with at least one row, the tool aborts with an actionable
/// error pointing operators at the service.
/// </summary>
internal static class HydrationAssertion
{
    public static async Task<bool> AssertHydratedAsync(string dbPath, TextWriter stderr, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);
        ArgumentNullException.ThrowIfNull(stderr);

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        await using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using (SqliteCommand probe = connection.CreateCommand())
        {
            probe.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'prepared_ticket_hydration'";
            object? exists = await probe.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (exists is null)
            {
                await WriteActionableErrorAsync(stderr, dbPath).ConfigureAwait(false);
                return false;
            }
        }

        await using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM prepared_ticket_hydration";
        object? rowCount = await count.ExecuteScalarAsync(ct).ConfigureAwait(false);
        long n = rowCount is long l ? l : Convert.ToInt64(rowCount);
        if (n > 0)
        {
            return true;
        }

        await WriteActionableErrorAsync(stderr, dbPath).ConfigureAwait(false);
        return false;
    }

    private static Task WriteActionableErrorAsync(TextWriter stderr, string dbPath)
        => stderr.WriteLineAsync(
            $"Database '{dbPath}' is not hydrated. Run FhirAugury.Processor.Jira.Fhir.Preparer against it first "
            + "(the service hydrates on startup, or POST /api/v1/admin/hydration/backfill on a running service).");
}
