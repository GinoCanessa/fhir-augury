using System.CommandLine;
using FhirAugury.Database;
using FhirAugury.Indexing;
using FhirAugury.Indexing.Bm25;

namespace FhirAugury.Cli.Commands;

public static class IndexCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> verboseOption)
    {
        var command = new Command("index", "Build or rebuild search indexes");

        var buildFtsCommand = new Command("build-fts", "Populate FTS5 tables from content tables");
        var buildBm25Command = new Command("build-bm25", "Build/rebuild BM25 keyword index");
        var buildXrefCommand = new Command("build-xref", "Build/rebuild cross-reference links");
        var rebuildAllCommand = new Command("rebuild-all", "Full rebuild of all indexes (FTS5 + BM25 + xref)");

        buildFtsCommand.SetAction((parseResult, _) =>
        {
            var dbPath = parseResult.GetValue(dbOption)!;
            RebuildFts(dbPath, parseResult.GetValue(verboseOption));
            return Task.CompletedTask;
        });

        buildBm25Command.SetAction((parseResult, ct) =>
        {
            var dbPath = parseResult.GetValue(dbOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            BuildBm25(dbPath, verbose, ct);
            return Task.CompletedTask;
        });

        buildXrefCommand.SetAction((parseResult, ct) =>
        {
            var dbPath = parseResult.GetValue(dbOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            BuildXref(dbPath, verbose, ct);
            return Task.CompletedTask;
        });

        rebuildAllCommand.SetAction((parseResult, ct) =>
        {
            var dbPath = parseResult.GetValue(dbOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            RebuildFts(dbPath, verbose);
            BuildBm25(dbPath, verbose, ct);
            BuildXref(dbPath, verbose, ct);
            Console.WriteLine("All indexes rebuilt successfully.");
            return Task.CompletedTask;
        });

        command.Add(buildFtsCommand);
        command.Add(buildBm25Command);
        command.Add(buildXrefCommand);
        command.Add(rebuildAllCommand);

        return command;
    }

    private static void RebuildFts(string dbPath, bool verbose)
    {
        var dbService = new DatabaseService(dbPath);
        dbService.InitializeDatabase();

        using var conn = dbService.OpenConnection();

        if (verbose) Console.WriteLine("Rebuilding Jira FTS5 indexes...");
        FtsSetup.RebuildJiraFts(conn);

        if (verbose) Console.WriteLine("Rebuilding Zulip FTS5 indexes...");
        FtsSetup.RebuildZulipFts(conn);

        Console.WriteLine("FTS5 indexes rebuilt successfully.");
    }

    private static void BuildBm25(string dbPath, bool verbose, CancellationToken ct)
    {
        var dbService = new DatabaseService(dbPath);
        dbService.InitializeDatabase();

        using var conn = dbService.OpenConnection();

        if (verbose) Console.WriteLine("Building BM25 keyword index...");
        Bm25Calculator.BuildFullIndex(conn, ct: ct);

        Console.WriteLine("BM25 keyword index built successfully.");
    }

    private static void BuildXref(string dbPath, bool verbose, CancellationToken ct)
    {
        var dbService = new DatabaseService(dbPath);
        dbService.InitializeDatabase();

        using var conn = dbService.OpenConnection();

        if (verbose) Console.WriteLine("Building cross-reference links...");
        CrossRefLinker.RebuildAllLinks(conn, ct);

        Console.WriteLine("Cross-reference links built successfully.");
    }
}
