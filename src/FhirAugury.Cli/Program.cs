using System.CommandLine;
using FhirAugury.Cli.Commands;

namespace FhirAugury.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("FHIR Augury — unified knowledge platform CLI");

        // Global options
        var orchestratorOption = new Option<string>("--orchestrator")
        {
            Description = "Orchestrator gRPC address",
            DefaultValueFactory = _ =>
                Environment.GetEnvironmentVariable("FHIR_AUGURY_ORCHESTRATOR") ?? "http://localhost:5151",
        };
        var formatOption = new Option<string>("--format")
        {
            Description = "Output format: table, json, or markdown",
            DefaultValueFactory = _ => "table",
        };
        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Verbose output",
            DefaultValueFactory = _ => false,
        };

        rootCommand.Add(orchestratorOption);
        rootCommand.Add(formatOption);
        rootCommand.Add(verboseOption);

        // Register commands
        rootCommand.Add(SearchCommand.Create(orchestratorOption, formatOption, verboseOption));
        rootCommand.Add(RelatedCommand.Create(orchestratorOption, formatOption, verboseOption));
        rootCommand.Add(GetCommand.Create(orchestratorOption, formatOption, verboseOption));
        rootCommand.Add(SnapshotCommand.Create(orchestratorOption, verboseOption));
        rootCommand.Add(XrefCommand.Create(orchestratorOption, formatOption, verboseOption));
        rootCommand.Add(IngestCommand.Create(orchestratorOption, verboseOption));
        rootCommand.Add(ServicesCommand.Create(orchestratorOption, formatOption, verboseOption));
        rootCommand.Add(QueryJiraCommand.Create(orchestratorOption, formatOption, verboseOption));
        rootCommand.Add(QueryZulipCommand.Create(orchestratorOption, formatOption, verboseOption));
        rootCommand.Add(ListCommand.Create(orchestratorOption, formatOption, verboseOption));

        var parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync();
    }
}
