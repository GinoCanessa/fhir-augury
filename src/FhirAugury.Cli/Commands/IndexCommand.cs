using System.CommandLine;
using FhirAugury.Database;

namespace FhirAugury.Cli.Commands;

public static class IndexCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> verboseOption)
    {
        var command = new Command("index", "Build or rebuild search indexes");

        var buildFtsCommand = new Command("build-fts", "Populate FTS5 tables from content tables");
        var rebuildAllCommand = new Command("rebuild-all", "Full rebuild of all FTS5 indexes");

        buildFtsCommand.SetAction((parseResult, _) =>
        {
            var dbPath = parseResult.GetValue(dbOption)!;
            RebuildFts(dbPath, parseResult.GetValue(verboseOption));
            return Task.CompletedTask;
        });

        rebuildAllCommand.SetAction((parseResult, _) =>
        {
            var dbPath = parseResult.GetValue(dbOption)!;
            RebuildFts(dbPath, parseResult.GetValue(verboseOption));
            return Task.CompletedTask;
        });

        command.Add(buildFtsCommand);
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
}
