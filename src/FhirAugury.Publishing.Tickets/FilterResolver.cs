using Microsoft.Data.Sqlite;

namespace FhirAugury.Publishing.Tickets;

internal static class FilterResolver
{
    public static async Task<ResolvedFilters> ResolveAsync(
        string dbPath,
        TicketSiteFilters requested,
        TicketSiteKind siteKind,
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
        if (!string.IsNullOrEmpty(requested.Specification))
        {
            List<string> values = await GetDistinctAsync(
                connection,
                siteKind == TicketSiteKind.Applying
                    ? "SELECT DISTINCT Specification FROM planned_ticket_hydration WHERE Specification IS NOT NULL"
                    : "SELECT DISTINCT Specification FROM prepared_ticket_hydration WHERE Specification IS NOT NULL",
                ct).ConfigureAwait(false);
            canonicalSpec = MatchCaseInsensitive(values, requested.Specification);
            if (canonicalSpec is null)
            {
                throw Unknown("--spec", requested.Specification, values);
            }
        }

        string? canonicalProject = null;
        if (!string.IsNullOrEmpty(requested.Project))
        {
            List<string> values = await GetDistinctAsync(
                connection,
                siteKind == TicketSiteKind.Applying
                    ? "SELECT DISTINCT substr(Key, 1, instr(Key, '-') - 1) FROM planned_tickets WHERE instr(Key, '-') > 1"
                    : "SELECT DISTINCT substr(Key, 1, instr(Key, '-') - 1) FROM prepared_tickets WHERE instr(Key, '-') > 1",
                ct).ConfigureAwait(false);
            canonicalProject = MatchCaseInsensitive(values, requested.Project);
            if (canonicalProject is null)
            {
                throw Unknown("--project", requested.Project, values);
            }
        }

        string? canonicalWorkGroup = null;
        if (!string.IsNullOrEmpty(requested.WorkGroup))
        {
            List<string> wgValues = await GetDistinctAsync(
                connection,
                siteKind == TicketSiteKind.Applying
                    ? "SELECT DISTINCT WorkGroup FROM planned_jira_hydration WHERE IssueKey = JiraKey AND WorkGroup IS NOT NULL"
                    : "SELECT DISTINCT WorkGroup FROM prepared_jira_hydration WHERE TicketKey = JiraKey AND WorkGroup IS NOT NULL",
                ct).ConfigureAwait(false);

            string? directMatch = MatchCaseInsensitive(
                wgValues,
                requested.WorkGroup);
            if (directMatch is not null)
            {
                canonicalWorkGroup = directMatch;
            }
            else
            {
                string? resolved = await WorkGroupResolver.TryResolveAsync(
                    requested.WorkGroup,
                    dbPath,
                    ct).ConfigureAwait(false);

                if (resolved is not null)
                {
                    canonicalWorkGroup = MatchCaseInsensitive(wgValues, resolved);
                }

                if (canonicalWorkGroup is null)
                {
                    throw Unknown("--wg", requested.WorkGroup, wgValues);
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

    private static TicketSitePublishException Unknown(
        string flag,
        string raw,
        List<string> values)
    {
        List<string> sorted = [.. values];
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        string available = sorted.Count == 0
            ? $"No values are present for {flag} in this processor snapshot."
            : $"Available values:{Environment.NewLine}{string.Join(Environment.NewLine, sorted)}";
        return new TicketSitePublishException(
            TicketSitePublishFailure.FilterValidation,
            $"Unknown value for {flag}: '{raw}'.{Environment.NewLine}{available}");
    }
}
