using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketSite;

internal static class FilterResolver
{
    public static async Task<ResolvedFilters?> TryResolveAsync(
        string dbPath,
        CliOptions cli,
        string kind,
        TextWriter stderr,
        CancellationToken ct)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        await using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        string? canonicalSpec = null;
        if (!string.IsNullOrEmpty(cli.FilterSpec))
        {
            List<string> values = await GetDistinctAsync(
                connection,
                kind == PlannerSubSiteEmitter.Kind
                    ? "SELECT DISTINCT Specification FROM planned_ticket_hydration WHERE Specification IS NOT NULL"
                    : "SELECT DISTINCT Specification FROM prepared_ticket_hydration WHERE Specification IS NOT NULL",
                ct).ConfigureAwait(false);
            canonicalSpec = MatchCaseInsensitive(values, cli.FilterSpec);
            if (canonicalSpec is null)
            {
                await WriteUnknownAsync(stderr, "--spec", cli.FilterSpec, values).ConfigureAwait(false);
                return null;
            }
        }

        string? canonicalProject = null;
        if (!string.IsNullOrEmpty(cli.FilterProject))
        {
            List<string> values = await GetDistinctAsync(
                connection,
                kind == PlannerSubSiteEmitter.Kind
                    ? "SELECT DISTINCT substr(Key, 1, instr(Key, '-') - 1) FROM planned_tickets WHERE instr(Key, '-') > 1"
                    : "SELECT DISTINCT substr(Key, 1, instr(Key, '-') - 1) FROM prepared_tickets WHERE instr(Key, '-') > 1",
                ct).ConfigureAwait(false);
            canonicalProject = MatchCaseInsensitive(values, cli.FilterProject);
            if (canonicalProject is null)
            {
                await WriteUnknownAsync(stderr, "--project", cli.FilterProject, values).ConfigureAwait(false);
                return null;
            }
        }

        string? canonicalWorkGroup = null;
        if (!string.IsNullOrEmpty(cli.FilterWorkGroup))
        {
            List<string> wgValues = await GetDistinctAsync(
                connection,
                kind == PlannerSubSiteEmitter.Kind
                    ? "SELECT DISTINCT WorkGroup FROM planned_jira_hydration WHERE IssueKey = JiraKey AND WorkGroup IS NOT NULL"
                    : "SELECT DISTINCT WorkGroup FROM prepared_jira_hydration WHERE TicketKey = JiraKey AND WorkGroup IS NOT NULL",
                ct).ConfigureAwait(false);

            string? directMatch = MatchCaseInsensitive(wgValues, cli.FilterWorkGroup);
            if (directMatch is not null)
            {
                canonicalWorkGroup = directMatch;
            }
            else
            {
                string? resolved = await WorkGroupResolver.TryResolveAsync(
                    cli.FilterWorkGroup,
                    dbPath,
                    ct).ConfigureAwait(false);

                if (resolved is not null)
                {
                    canonicalWorkGroup = MatchCaseInsensitive(wgValues, resolved);
                }

                if (canonicalWorkGroup is null)
                {
                    await WriteUnknownAsync(
                        stderr,
                        "--wg",
                        cli.FilterWorkGroup,
                        wgValues).ConfigureAwait(false);
                    return null;
                }
            }
        }

        return new ResolvedFilters(canonicalSpec, canonicalProject, canonicalWorkGroup);
    }

    private static async Task<List<string>> GetDistinctAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        List<string> values = [];
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                values.Add(reader.GetString(0));
            }
        }
        return values;
    }

    private static string? MatchCaseInsensitive(List<string> values, string candidate)
    {
        foreach (string value in values)
        {
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return null;
    }

    private static async Task WriteUnknownAsync(
        TextWriter stderr,
        string flag,
        string raw,
        List<string> values)
    {
        await stderr.WriteLineAsync($"Unknown value for {flag}: '{raw}'.").ConfigureAwait(false);
        if (values.Count == 0)
        {
            await stderr.WriteLineAsync(
                $"No values are present for {flag} in this processor snapshot.")
                .ConfigureAwait(false);
            return;
        }
        await stderr.WriteLineAsync("Available values:").ConfigureAwait(false);
        List<string> sorted = [.. values];
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string value in sorted)
        {
            await stderr.WriteLineAsync(value).ConfigureAwait(false);
        }
    }
}
