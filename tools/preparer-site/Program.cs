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

        Console.WriteLine($"db:    {resolvedDb}");
        Console.WriteLine($"out:   {resolvedOut}");
        Console.WriteLine($"title: {title}");
        Console.WriteLine($"prune: {(prune ? "true" : "false")}");

        await Task.CompletedTask.ConfigureAwait(false);
        return 0;
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
