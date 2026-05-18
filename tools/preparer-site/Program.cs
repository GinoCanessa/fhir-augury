using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.PreparerSite;

public static class Program
{
    private const string DefaultTitle = "Preparer Report";
    private const string DefaultOutSubpath = "cache/jira-preparer-site";

    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArgs(args, out CliOptions options, out string? parseError))
        {
            await Console.Error.WriteLineAsync(parseError).ConfigureAwait(false);
            WriteUsage(Console.Error);
            return 2;
        }

        if (options.Help)
        {
            WriteUsage(Console.Error);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(options.DbPath))
        {
            await Console.Error.WriteLineAsync("Missing required argument: --db <path>").ConfigureAwait(false);
            WriteUsage(Console.Error);
            return 1;
        }

        string resolvedDb = Path.GetFullPath(options.DbPath);
        string resolvedOut = Path.GetFullPath(options.OutPath ?? Path.Combine(Directory.GetCurrentDirectory(), DefaultOutSubpath));
        string title = options.Title;

        if (!File.Exists(resolvedDb))
        {
            await Console.Error.WriteLineAsync($"Database file not found: {resolvedDb}").ConfigureAwait(false);
            return 1;
        }

        if (options.BackfillSpec)
        {
            return await SpecificationBackfill.RunAsync(resolvedDb, options, Console.Error, CancellationToken.None)
                .ConfigureAwait(false);
        }

        HydrationPreflight.PreflightResult preflight = await HydrationPreflight.RunAsync(
            resolvedDb, options, Console.Error, CancellationToken.None).ConfigureAwait(false);
        if (!preflight.Proceed)
        {
            return 1;
        }

        long preparedCount;
        try
        {
            preparedCount = await CountPreparedTicketsAsync(resolvedDb).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            await Console.Error.WriteLineAsync(
                $"Database schema error: cannot read 'prepared_tickets' from {resolvedDb}: {ex.Message}")
                .ConfigureAwait(false);
            return 1;
        }

        ResolvedFilters? filters;
        try
        {
            filters = await FilterResolver.TryResolveAsync(resolvedDb, options, Console.Error, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            await Console.Error.WriteLineAsync(
                $"Database schema error while resolving filters from {resolvedDb}: {ex.Message}")
                .ConfigureAwait(false);
            return 1;
        }

        if (filters is null)
        {
            return 1;
        }

        EchoResolvedFilter("--spec", options.FilterSpec, filters.Specification);
        EchoResolvedFilter("--project", options.FilterProject, filters.Project);
        EchoResolvedFilter("--wg", options.FilterWorkGroup, filters.WorkGroup);

        if (Directory.Exists(resolvedOut) && !options.Force)
        {
            MetaFilterSet? existing = OutputDirGuard.TryReadExistingMarker(resolvedOut);
            if (existing is not null && !OutputDirGuard.FilterSetsMatch(existing, filters))
            {
                await Console.Error.WriteLineAsync(
                    $"Output directory '{resolvedOut}' was produced with a different filter set. " +
                    "Pass --force to overwrite, or choose a different --out.")
                    .ConfigureAwait(false);
                return 1;
            }
        }

        // Always run through the temp-DB build pipeline so that downstream
        // steps (related-fields backfill below) have a consistent seam to
        // hang off of. With no active filters the trim DELETE is a no-op
        // (its WHERE clause collapses to TRUE for NULL bound params) and
        // the surviving count equals the source count.
        PreparerDbTrimmer.BuildResult built =
            await PreparerDbTrimmer.BuildAsync(resolvedDb, filters, CancellationToken.None).ConfigureAwait(false);
        byte[] dbBytes;
        try
        {
            // Backfill the new prepared_ticket_artifacts / prepared_ticket_pages
            // child tables from the upstream Jira source DB, then VACUUM the
            // temp DB before reading bytes. Tables are always created so the
            // SPA's crosscut SQL never fails on a missing schema.
            await RelatedFieldsBackfill.ApplyAsync(
                built.TempDbPath, options.JiraSourceDbPath, Console.Error, CancellationToken.None)
                .ConfigureAwait(false);

            dbBytes = await File.ReadAllBytesAsync(built.TempDbPath).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(built.TempDbPath); } catch { /* best-effort */ }
        }
        long? filteredCount = filters.HasAnyFilter ? built.SurvivingTicketCount : null;

        SiteEmitter.Emit(resolvedOut, title, filters, dbBytes);
        OutputDirGuard.WriteMarker(resolvedOut, filters, DateTimeOffset.UtcNow);

        double inlinedMb = dbBytes.Length / 1024.0 / 1024.0;
        if (filteredCount is { } fc)
        {
            if (fc == 0)
            {
                Console.WriteLine("0 prepared tickets match this filter.");
            }
            Console.WriteLine(
                $"Wrote {fc} prepared tickets (filtered from {preparedCount}) to " +
                $"{Path.Combine(resolvedOut, "index.html")} (DB inlined: {inlinedMb:0.0} MB).");
        }
        else
        {
            Console.WriteLine(
                $"Wrote {preparedCount} prepared tickets to {Path.Combine(resolvedOut, "index.html")} " +
                $"(DB inlined: {inlinedMb:0.0} MB).");
        }

        return 0;
    }

    private static void EchoResolvedFilter(string flag, string? raw, string? canonical)
    {
        if (raw is null || canonical is null)
        {
            return;
        }
        if (!string.Equals(raw, canonical, StringComparison.Ordinal))
        {
            Console.WriteLine($"Resolved {flag} '{raw}' → '{canonical}'.");
        }
    }

    private static async Task<long> CountPreparedTicketsAsync(string dbPath)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        await using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM prepared_tickets";
        object? result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return result is long l ? l : Convert.ToInt64(result);
    }

    private static bool TryParseArgs(string[] args, out CliOptions options, out string? error)
    {
        string? dbPath = null;
        string? outPath = null;
        string title = DefaultTitle;
        string? filterSpec = null;
        string? filterProject = null;
        string? filterWorkGroup = null;
        string? jiraSourceUrl = null;
        string? jiraSourceDbPath = null;
        string? orchestratorAddress = null;
        bool noHydrate = false;
        bool force = false;
        bool backfillSpec = false;
        bool help = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--db":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    dbPath = args[++i];
                    break;
                case "--out":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    outPath = args[++i];
                    break;
                case "--title":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    title = args[++i];
                    break;
                case "--spec":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    filterSpec = args[++i];
                    break;
                case "--project":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    filterProject = args[++i];
                    break;
                case "--wg":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    filterWorkGroup = args[++i];
                    break;
                case "--jira-source":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    jiraSourceUrl = args[++i];
                    break;
                case "--jira-source-db":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    jiraSourceDbPath = args[++i];
                    break;
                case "--orchestrator":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    orchestratorAddress = args[++i];
                    break;
                case "--no-hydrate":
                    noHydrate = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--backfill-spec":
                    backfillSpec = true;
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                default:
                    options = Default();
                    error = $"Unknown argument: {arg}";
                    return false;
            }
        }

        options = new CliOptions(
            DbPath: dbPath,
            OutPath: outPath,
            Title: title,
            FilterSpec: filterSpec,
            FilterProject: filterProject,
            FilterWorkGroup: filterWorkGroup,
            JiraSourceUrl: jiraSourceUrl,
            JiraSourceDbPath: jiraSourceDbPath,
            OrchestratorAddress: orchestratorAddress,
            NoHydrate: noHydrate,
            Force: force,
            BackfillSpec: backfillSpec,
            Help: help);
        error = null;
        return true;

        static CliOptions Default() => new(null, null, DefaultTitle, null, null, null, null, null, null, false, false, false, false);
    }

    private static void WriteUsage(TextWriter w)
    {
        w.WriteLine();
        w.WriteLine("Usage: preparer-site --db <path> [--out <path>] [--title <string>]");
        w.WriteLine("                     [--spec <name>] [--project <key>] [--wg <name|code>]");
        w.WriteLine("                     [--jira-source <url>] [--jira-source-db <path>]");
        w.WriteLine("                     [--orchestrator <url>] [--no-hydrate] [--force]");
        w.WriteLine();
        w.WriteLine("  --db <path>            Path to the preparer SQLite database (required).");
        w.WriteLine("  --out <path>           Output directory (default: ./cache/jira-preparer-site).");
        w.WriteLine($"  --title <string>       Site title (default: \"{DefaultTitle}\").");
        w.WriteLine("  --spec <name>          Filter to tickets whose hydrated specification matches (case-insensitive).");
        w.WriteLine("  --project <key>        Filter to tickets in the given Jira project key (case-insensitive).");
        w.WriteLine("  --wg <name|code>       Filter to tickets in the given workgroup; matches name, code, or clean name.");
        w.WriteLine("  --jira-source <url>    Base URL of the Jira source service for --wg code resolution");
        w.WriteLine("                         (default: http://localhost:5160).");
        w.WriteLine("  --jira-source-db <path> Fallback Jira source SQLite DB when the HTTP service is unreachable.");
        w.WriteLine("  --orchestrator <url>   Base URL of the orchestrator used for opportunistic hydration");
        w.WriteLine($"                         (default: {HydrationHttpClient.DefaultOrchestratorAddress}).");
        w.WriteLine("  --no-hydrate           Skip auto-hydration; fail fast if the DB lacks hydration rows.");
        w.WriteLine("  --force                Overwrite an output directory produced with a different filter set.");
        w.WriteLine("  --backfill-spec        Backfill the Specification column on jira_processing_source_tickets");
        w.WriteLine("                         from the Jira source service / DB; do not emit the site.");
        w.WriteLine("  --help                 Show this help.");
    }
}

