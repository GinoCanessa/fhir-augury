using System.CommandLine;
using FhirAugury.Cli.Commands;

namespace FhirAugury.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("FHIR Augury — unified knowledge platform CLI");

        // Global options
        var dbOption = new Option<string>("--db")
        {
            Description = "Database file path",
            DefaultValueFactory = _ => "fhir-augury.db",
        };
        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Verbose output",
            DefaultValueFactory = _ => false,
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Force JSON output format",
            DefaultValueFactory = _ => false,
        };

        rootCommand.Add(dbOption);
        rootCommand.Add(verboseOption);
        rootCommand.Add(jsonOption);

        // Register commands
        rootCommand.Add(DownloadCommand.Create(dbOption, verboseOption, jsonOption));
        rootCommand.Add(SyncCommand.Create(dbOption, verboseOption, jsonOption));
        rootCommand.Add(IngestCommand.Create(dbOption, verboseOption, jsonOption));
        rootCommand.Add(IndexCommand.Create(dbOption, verboseOption));
        rootCommand.Add(SearchCommand.Create(dbOption, verboseOption, jsonOption));
        rootCommand.Add(GetCommand.Create(dbOption, verboseOption, jsonOption));
        rootCommand.Add(SnapshotCommand.Create(dbOption, verboseOption));
        rootCommand.Add(StatsCommand.Create(dbOption, jsonOption));

        var parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync();
    }
}
