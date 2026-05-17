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

        byte[] dbBytes = await File.ReadAllBytesAsync(resolvedDb).ConfigureAwait(false);

        SiteEmitter.Emit(resolvedOut, title, dbBytes);

        double inlinedMb = dbBytes.Length / 1024.0 / 1024.0;
        Console.WriteLine(
            $"Wrote {preparedCount} prepared tickets to {Path.Combine(resolvedOut, "index.html")} " +
            $"(DB inlined: {inlinedMb:0.0} MB).");

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
        w.WriteLine("Usage: preparer-site --db <path> [--out <path>] [--title <string>]");
        w.WriteLine();
        w.WriteLine("  --db <path>       Path to the preparer SQLite database (required).");
        w.WriteLine("  --out <path>      Output directory (default: ./cache/jira-preparer-site).");
        w.WriteLine($"  --title <string>  Site title (default: \"{DefaultTitle}\").");
        w.WriteLine("  --help            Show this help.");
    }
}

