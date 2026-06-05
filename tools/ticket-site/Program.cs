using Microsoft.Data.Sqlite;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;

namespace FhirAugury.Tools.TicketSite;

public static class Program
{
    private const string DefaultTitle = "Ticket Site";
    private const string DefaultOutSubpath = "cache/jira-ticket-site";
    private const string DefaultPreparerDb = "./cache/jira-preparer.db";
    private const string DefaultPlannerDb = "./cache/jira-planner.db";

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
            WriteUsage(Console.Out);
            return 0;
        }

        // XOR check for the DB-pointer flags (Phase 5 step 3 validation order).
        if (options.PreparerDbSupplied && options.PlannerDbSupplied)
        {
            await Console.Error.WriteLineAsync(
                "--preparer-db and --planner-db are mutually exclusive; build one sub-site per invocation.")
                .ConfigureAwait(false);
            return 2;
        }
        if (!options.PreparerDbSupplied && !options.PlannerDbSupplied)
        {
            await Console.Error.WriteLineAsync("Specify either --preparer-db or --planner-db.").ConfigureAwait(false);
            WriteUsage(Console.Error);
            return 2;
        }

        string kind;
        string subSiteFolder;
        string dbPath;
        if (options.PreparerDbSupplied)
        {
            kind = PreparerSubSiteEmitter.Kind;
            subSiteFolder = PreparerSubSiteEmitter.SubSiteFolder;
            dbPath = string.IsNullOrEmpty(options.PreparerDbPath) ? DefaultPreparerDb : options.PreparerDbPath;
        }
        else
        {
            kind = PlannerSubSiteEmitter.Kind;
            subSiteFolder = PlannerSubSiteEmitter.SubSiteFolder;
            dbPath = string.IsNullOrEmpty(options.PlannerDbPath) ? DefaultPlannerDb : options.PlannerDbPath;
        }

        string resolvedDb = Path.GetFullPath(dbPath);
        string rootOut = Path.GetFullPath(options.OutPath ?? Path.Combine(Directory.GetCurrentDirectory(), DefaultOutSubpath));
        string subSiteOut = Path.Combine(rootOut, subSiteFolder);
        string title = options.Title;

        if (!File.Exists(resolvedDb))
        {
            await Console.Error.WriteLineAsync($"Database file not found: {resolvedDb}").ConfigureAwait(false);
            return 1;
        }

        if (kind == PreparerSubSiteEmitter.Kind)
        {
            int exit = await EmitPreparerSubSiteAsync(options, resolvedDb, rootOut, subSiteOut, title);
            if (exit != 0) return exit;
        }
        else
        {
            int exit = await EmitPlannerSubSiteAsync(options, resolvedDb, rootOut, subSiteOut, title);
            if (exit != 0) return exit;
        }

        // Always regenerate the chooser after a sub-site emit. It scans the
        // root dir for which sub-sites exist; no marker file of its own.
        ChooserPageEmitter.Emit(rootOut);
        Console.WriteLine($"Updated chooser at {Path.Combine(rootOut, "index.html")}.");
        return 0;

        async Task<int> EmitPreparerSubSiteAsync(CliOptions opts, string db, string root, string subOut, string siteTitle)
        {
            bool hydrated = await HydrationAssertion.AssertHydratedAsync(db, Console.Error, CancellationToken.None).ConfigureAwait(false);
            if (!hydrated) return 1;

            long preparedCount;
            try
            {
                preparedCount = await CountAsync(db, "SELECT count(*) FROM prepared_tickets").ConfigureAwait(false);
            }
            catch (SqliteException ex)
            {
                await Console.Error.WriteLineAsync($"Database schema error: cannot read 'prepared_tickets' from {db}: {ex.Message}").ConfigureAwait(false);
                return 1;
            }

            ResolvedFilters? f = await FilterResolver.TryResolveAsync(db, opts, Console.Error, CancellationToken.None).ConfigureAwait(false);
            if (f is null) return 1;

            EchoResolvedFilter("--spec", opts.FilterSpec, f.Specification);
            EchoResolvedFilter("--project", opts.FilterProject, f.Project);
            EchoResolvedFilter("--wg", opts.FilterWorkGroup, f.WorkGroup);

            if (!CheckGuard(subOut, PreparerSubSiteEmitter.Kind, f, opts.Force, out string? guardError))
            {
                await Console.Error.WriteLineAsync(guardError!).ConfigureAwait(false);
                return 1;
            }

            PreparerDbTrimmer.BuildResult built =
                await PreparerDbTrimmer.BuildAsync(db, f, CancellationToken.None).ConfigureAwait(false);
            byte[] dbBytes;
            try
            {
                await RelatedFieldsBackfill.ApplyAsync(built.TempDbPath, opts.JiraSourceDbPath, Console.Error, CancellationToken.None).ConfigureAwait(false);
                dbBytes = await ReadAllBytesWithTransientRetryAsync(built.TempDbPath).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(built.TempDbPath); } catch { }
            }

            long? filteredCount = f.HasAnyFilter ? built.SurvivingTicketCount : null;
            PreparerSubSiteEmitter.Emit(subOut, siteTitle, f, dbBytes);
            OutputDirGuard.WriteMarker(subOut, PreparerSubSiteEmitter.Kind, f, DateTimeOffset.UtcNow);

            double inlinedMb = dbBytes.Length / 1024.0 / 1024.0;
            if (filteredCount is { } fc)
            {
                if (fc == 0) Console.WriteLine("0 prepared tickets match this filter.");
                Console.WriteLine($"Wrote {fc} prepared tickets (filtered from {preparedCount}) to {Path.Combine(subOut, "index.html")} (DB inlined: {inlinedMb:0.0} MB).");
            }
            else
            {
                Console.WriteLine($"Wrote {preparedCount} prepared tickets to {Path.Combine(subOut, "index.html")} (DB inlined: {inlinedMb:0.0} MB).");
            }
            return 0;
        }

        async Task<int> EmitPlannerSubSiteAsync(CliOptions opts, string db, string root, string subOut, string siteTitle)
        {
            ResolvedFilters f = new(opts.FilterSpec, opts.FilterProject, opts.FilterWorkGroup);
            if (!CheckGuard(subOut, PlannerSubSiteEmitter.Kind, f, opts.Force, out string? guardError))
            {
                await Console.Error.WriteLineAsync(guardError!).ConfigureAwait(false);
                return 1;
            }

            // Always go through the trim pipeline so older planner DBs self-migrate
            // and so downstream emit sees a consistent (filter, VACUUM) DB shape.
            PlannerDbTrimmer.BuildResult built;
            try
            {
                built = await PlannerDbTrimmer.BuildAsync(db, f, CancellationToken.None).ConfigureAwait(false);
            }
            catch (SqliteException ex)
            {
                await Console.Error.WriteLineAsync($"Database schema error: cannot trim planner DB at {db}: {ex.Message}").ConfigureAwait(false);
                return 1;
            }

            try
            {
                long plannedCount = built.SurvivingTicketCount;
                byte[] dbBytes = await ReadAllBytesWithTransientRetryAsync(built.TempDbPath).ConfigureAwait(false);
                PlannerSubSiteEmitter.Emit(subOut, siteTitle, f, dbBytes);
                OutputDirGuard.WriteMarker(subOut, PlannerSubSiteEmitter.Kind, f, DateTimeOffset.UtcNow);

                double inlinedMb = dbBytes.Length / 1024.0 / 1024.0;
                if (f.HasAnyFilter && plannedCount == 0)
                {
                    Console.WriteLine("0 planned tickets match this filter.");
                }
                Console.WriteLine($"Wrote {plannedCount} planned tickets to {Path.Combine(subOut, "index.html")} (DB inlined: {inlinedMb:0.0} MB).");
                return 0;
            }
            finally
            {
                try { File.Delete(built.TempDbPath); } catch { }
            }
        }
    }

    private static bool CheckGuard(string subSiteOut, string kind, ResolvedFilters filters, bool force, out string? error)
    {
        error = null;
        if (!Directory.Exists(subSiteOut)) return true;
        MetaFilterSet? existing = OutputDirGuard.TryReadExistingMarker(subSiteOut);
        if (existing is null) return true;

        if (!OutputDirGuard.KindMatches(existing, kind))
        {
            error = $"Output sub-site directory '{subSiteOut}' was produced as kind '{existing.Kind}' but the current build is '{kind}'. " +
                    "This should not occur by construction; resolve the conflict before re-running.";
            return false;
        }

        if (!force && !OutputDirGuard.FilterSetsMatch(existing, filters))
        {
            error = $"Output sub-site directory '{subSiteOut}' was produced with a different filter set. Pass --force to overwrite, or choose a different --out.";
            return false;
        }

        return true;
    }

    private static void EchoResolvedFilter(string flag, string? raw, string? canonical)
    {
        if (raw is null || canonical is null) return;
        if (!string.Equals(raw, canonical, StringComparison.Ordinal))
        {
            Console.WriteLine($"Resolved {flag} '{raw}' → '{canonical}'.");
        }
    }

    private static async Task<long> CountAsync(string dbPath, string sql)
    {
        SqliteConnectionStringBuilder builder = new() { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly };
        await using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        object? result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        return result is long l ? l : Convert.ToInt64(result);
    }

    // Read the just-trimmed temp DB tolerating a transient Windows sharing
    // violation. With Pooling=false on the temp-DB SqliteConnection (see
    // PreparerDbTrimmer / PlannerDbTrimmer / RelatedFieldsBackfill), the
    // native file handle is released synchronously on Dispose; AV scanners
    // and the OS file-cache flush can then briefly hold the freshly
    // released file in a way that races a vanilla File.ReadAllBytesAsync.
    //
    // Approach:
    //   1. Open with FileShare.ReadWrite | Delete so any concurrent reader
    //      (AV scanner) that uses FileShare.Read does not block us.
    //   2. Retry on IOException / UnauthorizedAccessException with linear
    //      backoff up to ~10s total. Any genuine still-alive writer would
    //      persist longer than that, so a hard throw is still meaningful.
    //   3. Use stream.Length once at open and ReadExactly to guard against
    //      a silently truncated read — a short DB inlined into HTML would
    //      be worse than a loud failure.
    private static async Task<byte[]> ReadAllBytesWithTransientRetryAsync(string path)
    {
        const int maxAttempts = 20;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    useAsync: true);

                long length = stream.Length;
                if (length > int.MaxValue)
                {
                    throw new IOException($"Temp DB at '{path}' is too large to inline ({length} bytes).");
                }

                byte[] buffer = new byte[length];
                await stream.ReadExactlyAsync(buffer.AsMemory()).ConfigureAwait(false);
                return buffer;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt)).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt)).ConfigureAwait(false);
            }
        }
    }

    private static bool TryParseArgs(string[] args, out CliOptions options, out string? error)
    {
        string? preparerDb = null;
        bool preparerSupplied = false;
        string? plannerDb = null;
        bool plannerSupplied = false;
        string? outPath = null;
        string title = DefaultTitle;
        string? filterSpec = null;
        string? filterProject = null;
        string? filterWg = null;
        string? jiraSourceUrl = null;
        string? jiraSourceDbPath = null;
        bool force = false;
        bool help = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--preparer-db":
                    preparerSupplied = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) preparerDb = args[++i];
                    break;
                case "--planner-db":
                    plannerSupplied = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) plannerDb = args[++i];
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
                    filterWg = args[++i];
                    break;
                case "--jira-source":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    jiraSourceUrl = args[++i];
                    break;
                case "--jira-source-db":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    jiraSourceDbPath = args[++i];
                    break;
                case "--force":
                    force = true;
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
            PreparerDbPath: preparerDb,
            PreparerDbSupplied: preparerSupplied,
            PlannerDbPath: plannerDb,
            PlannerDbSupplied: plannerSupplied,
            OutPath: outPath,
            Title: title,
            FilterSpec: filterSpec,
            FilterProject: filterProject,
            FilterWorkGroup: filterWg,
            JiraSourceUrl: jiraSourceUrl,
            JiraSourceDbPath: jiraSourceDbPath,
            Force: force,
            Help: help);
        error = null;
        return true;

        static CliOptions Default() => new(null, false, null, false, null, DefaultTitle, null, null, null, null, null, false, false);
    }

    private static void WriteUsage(TextWriter w)
    {
        w.WriteLine();
        w.WriteLine("Usage: ticket-site (--preparer-db <path> | --planner-db <path>) [options]");
        w.WriteLine();
        w.WriteLine("  Exactly one of --preparer-db / --planner-db is required.");
        w.WriteLine($"  --preparer-db <path>   Path to preparer SQLite DB (default: {DefaultPreparerDb}). Builds discussion/.");
        w.WriteLine($"  --planner-db <path>    Path to planner SQLite DB (default: {DefaultPlannerDb}). Builds applying/.");
        w.WriteLine("  --out <path>           Output root (default: ./cache/jira-ticket-site).");
        w.WriteLine($"  --title <string>       Site title (default: \"{DefaultTitle}\").");
        w.WriteLine("  --spec <name>          Filter tickets by hydrated specification.");
        w.WriteLine("  --project <key>        Filter by Jira project key.");
        w.WriteLine("  --wg <name|code>       Filter by workgroup (name, code, or clean name).");
        w.WriteLine("  --jira-source <url>    Jira source service URL for --wg code resolution.");
        w.WriteLine("  --jira-source-db <path> Jira source SQLite DB (fallback).");
        w.WriteLine("  --force                Overwrite a sub-site dir whose marker has a different filter set.");
        w.WriteLine("  --help                 Show this help.");
    }
}
