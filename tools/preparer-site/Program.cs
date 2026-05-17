using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.PreparerSite;

public static class Program
{
    private const string DefaultTitle = "Preparer Report";
    private const string DefaultOutSubpath = "cache/jira-preparer-site";

    public static async Task<int> Main(string[] args)
    {
        string? dbPath = null;
        string? outPath = null;
        string title = DefaultTitle;
        bool prune = false;
        bool help = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--title" when i + 1 < args.Length:
                    title = args[++i];
                    break;
                case "--prune":
                    prune = true;
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                default:
                    await Console.Error.WriteLineAsync($"Unknown argument: {args[i]}").ConfigureAwait(false);
                    WriteUsage(Console.Error);
                    return 2;
            }
        }

        if (help)
        {
            WriteUsage(Console.Error);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(dbPath))
        {
            await Console.Error.WriteLineAsync("Missing required argument: --db <path>").ConfigureAwait(false);
            WriteUsage(Console.Error);
            return 1;
        }

        string resolvedDb = Path.GetFullPath(dbPath);
        string resolvedOut = Path.GetFullPath(outPath ?? Path.Combine(Directory.GetCurrentDirectory(), DefaultOutSubpath));

        if (!File.Exists(resolvedDb))
        {
            await Console.Error.WriteLineAsync($"Database file not found: {resolvedDb}").ConfigureAwait(false);
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

        // Phase 6 will substitute a pruned temp-DB path here when --prune is set.
        string sourceDbForInline = resolvedDb;
        string? prunedTempPath = null;
        long? prunedSize = null;
        try
        {
            if (prune)
            {
                prunedTempPath = Path.Combine(
                    Path.GetTempPath(),
                    "preparer-site-pruned-" + Guid.NewGuid().ToString("N") + ".db");
                DbPruner.Prune(resolvedDb, prunedTempPath);
                prunedSize = new FileInfo(prunedTempPath).Length;
                sourceDbForInline = prunedTempPath;
            }

            byte[] dbBytes = await File.ReadAllBytesAsync(sourceDbForInline).ConfigureAwait(false);

            SiteEmitter.Emit(resolvedOut, title, dbBytes);

            double inlinedMb = dbBytes.Length / 1024.0 / 1024.0;
            Console.WriteLine(
                $"Wrote {preparedCount} prepared tickets to {Path.Combine(resolvedOut, "index.html")} " +
                $"(DB inlined: {inlinedMb:0.0} MB{(prune ? "; pruned" : string.Empty)}).");

            if (prune && prunedSize is long pruned)
            {
                long sourceSize = new FileInfo(resolvedDb).Length;
                double sourceMb = sourceSize / 1024.0 / 1024.0;
                double prunedMb = pruned / 1024.0 / 1024.0;
                double savedPct = sourceSize > 0
                    ? (1.0 - (double)pruned / sourceSize) * 100.0
                    : 0.0;
                Console.WriteLine(
                    $"Pruned {sourceMb:0.0} MB → {prunedMb:0.0} MB (saved {savedPct:0.0}%).");
            }
        }
        finally
        {
            if (prunedTempPath is not null && File.Exists(prunedTempPath))
            {
                try { File.Delete(prunedTempPath); }
                catch (IOException) { /* best-effort cleanup */ }
            }
        }

        return 0;
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

    private static void WriteUsage(TextWriter w)
    {
        w.WriteLine();
        w.WriteLine("Usage: preparer-site --db <path> [--out <path>] [--title <string>] [--prune]");
        w.WriteLine();
        w.WriteLine("  --db <path>       Path to the preparer SQLite database (required).");
        w.WriteLine("  --out <path>      Output directory (default: ./cache/jira-preparer-site).");
        w.WriteLine($"  --title <string>  Site title (default: \"{DefaultTitle}\").");
        w.WriteLine("  --prune           Emit a slimmed copy of the DB (opt-in size reducer).");
        w.WriteLine("  --help            Show this help.");
    }
}

