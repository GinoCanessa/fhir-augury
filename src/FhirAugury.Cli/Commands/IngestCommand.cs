using System.CommandLine;
using Fhiraugury;
using FhirAugury.Cli.OutputFormatters;

namespace FhirAugury.Cli.Commands;

public static class IngestCommand
{
    public static Command Create(Option<string> orchestratorOption, Option<bool> verboseOption)
    {
        var command = new Command("ingest", "Ingestion and sync management");

        command.Add(CreateTriggerCommand(orchestratorOption, verboseOption));
        command.Add(CreateStatusCommand(orchestratorOption));
        command.Add(CreateRebuildCommand(orchestratorOption, verboseOption));

        return command;
    }

    private static Command CreateTriggerCommand(Option<string> orchestratorOption, Option<bool> verboseOption)
    {
        var sourcesOption = new Option<string?>("--sources")
        {
            Description = "Comma-separated sources to sync (empty for all)",
        };
        var typeOption = new Option<string>("--type")
        {
            Description = "Sync type: incremental, full, rebuild",
            DefaultValueFactory = _ => "incremental",
        };

        var command = new Command("trigger", "Trigger synchronization across source services")
        {
            sourcesOption,
            typeOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var addr = parseResult.GetValue(orchestratorOption)!;
            var sources = parseResult.GetValue(sourcesOption);
            var type = parseResult.GetValue(typeOption)!;

            using var clients = new GrpcClientFactory(addr);
            var request = new TriggerSyncRequest { Type = type };

            if (!string.IsNullOrWhiteSpace(sources))
            {
                foreach (var s in sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    request.Sources.Add(s.ToLowerInvariant());
            }

            var response = await clients.Orchestrator.TriggerSyncAsync(request, cancellationToken: ct);
            OutputFormatter.FormatSyncStatus(response, "table");
        });

        return command;
    }

    private static Command CreateStatusCommand(Option<string> orchestratorOption)
    {
        var command = new Command("status", "Get ingestion status");

        command.SetAction(async (parseResult, ct) =>
        {
            var addr = parseResult.GetValue(orchestratorOption)!;
            using var clients = new GrpcClientFactory(addr);
            var response = await clients.Orchestrator.GetServicesStatusAsync(
                new ServicesStatusRequest(), cancellationToken: ct);

            foreach (var svc in response.Services)
            {
                var lastSync = svc.LastSyncAt?.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm") ?? "never";
                Console.WriteLine($"{svc.Name}: {svc.Status} (last sync: {lastSync}, items: {svc.ItemCount})");
            }
        });

        return command;
    }

    private static Command CreateRebuildCommand(Option<string> orchestratorOption, Option<bool> verboseOption)
    {
        var sourcesOption = new Option<string?>("--sources")
        {
            Description = "Comma-separated sources to rebuild (empty for all)",
        };

        var command = new Command("rebuild", "Rebuild from cache")
        {
            sourcesOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var addr = parseResult.GetValue(orchestratorOption)!;
            var sources = parseResult.GetValue(sourcesOption);

            using var clients = new GrpcClientFactory(addr);
            var request = new TriggerSyncRequest { Type = "rebuild" };

            if (!string.IsNullOrWhiteSpace(sources))
            {
                foreach (var s in sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    request.Sources.Add(s.ToLowerInvariant());
            }

            var response = await clients.Orchestrator.TriggerSyncAsync(request, cancellationToken: ct);
            OutputFormatter.FormatSyncStatus(response, "table");
        });

        return command;
    }
}
