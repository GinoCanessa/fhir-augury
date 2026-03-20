using System.CommandLine;
using Fhiraugury;
using FhirAugury.Cli.OutputFormatters;

namespace FhirAugury.Cli.Commands;

public static class ServicesCommand
{
    public static Command Create(Option<string> orchestratorOption, Option<string> formatOption, Option<bool> verboseOption)
    {
        var command = new Command("services", "Service management and monitoring");

        command.Add(CreateStatusCommand(orchestratorOption, formatOption));
        command.Add(CreateStatsCommand(orchestratorOption, formatOption));
        command.Add(CreateXrefScanCommand(orchestratorOption, verboseOption));

        return command;
    }

    private static Command CreateStatusCommand(Option<string> orchestratorOption, Option<string> formatOption)
    {
        var command = new Command("status", "Get status of all connected services");

        command.SetAction(async (parseResult, ct) =>
        {
            var addr = parseResult.GetValue(orchestratorOption)!;
            var format = parseResult.GetValue(formatOption)!;

            using var clients = new GrpcClientFactory(addr);
            var response = await clients.Orchestrator.GetServicesStatusAsync(
                new ServicesStatusRequest(), cancellationToken: ct);
            OutputFormatter.FormatServicesStatus(response, format);
        });

        return command;
    }

    private static Command CreateStatsCommand(Option<string> orchestratorOption, Option<string> formatOption)
    {
        var command = new Command("stats", "Get aggregate statistics across all services");

        command.SetAction(async (parseResult, ct) =>
        {
            var addr = parseResult.GetValue(orchestratorOption)!;
            var format = parseResult.GetValue(formatOption)!;

            using var clients = new GrpcClientFactory(addr);
            var response = await clients.Orchestrator.GetServicesStatusAsync(
                new ServicesStatusRequest(), cancellationToken: ct);
            OutputFormatter.FormatServicesStatus(response, format);
        });

        return command;
    }

    private static Command CreateXrefScanCommand(Option<string> orchestratorOption, Option<bool> verboseOption)
    {
        var fullRescanOption = new Option<bool>("--full")
        {
            Description = "Force a full rescan instead of incremental",
            DefaultValueFactory = _ => false,
        };

        var command = new Command("xref-scan", "Trigger a cross-reference scan")
        {
            fullRescanOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var addr = parseResult.GetValue(orchestratorOption)!;
            var fullRescan = parseResult.GetValue(fullRescanOption);

            using var clients = new GrpcClientFactory(addr);
            var response = await clients.Orchestrator.TriggerXRefScanAsync(
                new TriggerXRefScanRequest { FullRescan = fullRescan },
                cancellationToken: ct);

            Console.WriteLine($"XRef scan: {response.Status} ({response.ItemsToScan} items to scan)");
        });

        return command;
    }
}
