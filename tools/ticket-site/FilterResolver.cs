using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketSite;

internal static class FilterResolver
{
    public static async Task<ResolvedFilters?> TryResolveAsync(
        string dbPath,
        CliOptions cli,
        TextWriter stderr,
        CancellationToken ct)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        await using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        string? canonicalSpec = null;
        if (!string.IsNullOrEmpty(cli.FilterSpec))
        {
            List<string> values = await GetDistinctAsync(
                connection,
                "SELECT DISTINCT Specification FROM prepared_ticket_hydration WHERE Specification IS NOT NULL",
                ct).ConfigureAwait(false);
            canonicalSpec = MatchCaseInsensitive(values, cli.FilterSpec);
            if (canonicalSpec is null)
            {
                await WriteUnknownAsync(stderr, "--spec", cli.FilterSpec, values, appendWgHint: false).ConfigureAwait(false);
                return null;
            }
        }

        string? canonicalProject = null;
        if (!string.IsNullOrEmpty(cli.FilterProject))
        {
            List<string> values = await GetDistinctAsync(
                connection,
                "SELECT DISTINCT Project FROM jira_processing_source_tickets WHERE Project IS NOT NULL",
                ct).ConfigureAwait(false);
            canonicalProject = MatchCaseInsensitive(values, cli.FilterProject);
            if (canonicalProject is null)
            {
                await WriteUnknownAsync(stderr, "--project", cli.FilterProject, values, appendWgHint: false).ConfigureAwait(false);
                return null;
            }
        }

        string? canonicalWorkGroup = null;
        if (!string.IsNullOrEmpty(cli.FilterWorkGroup))
        {
            List<string> wgValues = await GetDistinctAsync(
                connection,
                "SELECT DISTINCT WorkGroup FROM jira_processing_source_tickets WHERE WorkGroup IS NOT NULL",
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
                    cli.JiraSourceUrl,
                    cli.JiraSourceDbPath,
                    stderr,
                    ct).ConfigureAwait(false);

                if (resolved is not null)
                {
                    canonicalWorkGroup = MatchCaseInsensitive(wgValues, resolved);
                }

                if (canonicalWorkGroup is null)
                {
                    await WriteUnknownAsync(stderr, "--wg", cli.FilterWorkGroup, wgValues, appendWgHint: true).ConfigureAwait(false);
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
        List<string> values,
        bool appendWgHint)
    {
        await stderr.WriteLineAsync($"Unknown value for {flag}: '{raw}'.").ConfigureAwait(false);
        if (values.Count == 0)
        {
            await stderr.WriteLineAsync(
                $"No values are present for {flag} in the database. The DB may not be hydrated yet — " +
                "run FhirAugury.Processor.Jira.Fhir.Preparer against it to populate hydration.")
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
        if (appendWgHint)
        {
            await stderr.WriteLineAsync(
                "To match by code, ensure the Jira source service is reachable or pass --jira-source-db <path>.")
                .ConfigureAwait(false);
        }
    }
}
