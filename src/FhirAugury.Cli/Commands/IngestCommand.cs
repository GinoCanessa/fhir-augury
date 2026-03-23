using System.CommandLine;
using Fhiraugury;
using FhirAugury.Cli.OutputFormatters;
using Grpc.Core;

namespace FhirAugury.Cli.Commands;

public static class IngestCommand
{
    public static Command Create(Option<string> orchestratorOption, Option<string> formatOption, Option<bool> verboseOption)
    {
        var command = new Command("ingest", "Ingestion and sync management");

        command.Add(CreateTriggerCommand(orchestratorOption, formatOption, verboseOption));
        command.Add(CreateStatusCommand(orchestratorOption));
        command.Add(CreateRebuildCommand(orchestratorOption, formatOption, verboseOption));

        return command;
    }

    private static Command CreateTriggerCommand(Option<string> orchestratorOption, Option<string> formatOption, Option<bool> verboseOption)
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
            var verbose = parseResult.GetValue(verboseOption);
            try
            {
                var format = parseResult.GetValue(formatOption)!;
                var sources = parseResult.GetValue(sourcesOption);
                var type = parseResult.GetValue(typeOption)!;

                var sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
                using var clients = new GrpcClientFactory(addr);
                var request = new TriggerSyncRequest { Type = type };

                if (!string.IsNullOrWhiteSpace(sources))
                {
                    foreach (var s in sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        request.Sources.Add(s.ToLowerInvariant());
                }

                var response = await clients.Orchestrator.TriggerSyncAsync(request, cancellationToken: ct);
                OutputFormatter.FormatSyncStatus(response, format);
                if (sw is not null)
                    Console.Error.WriteLine($"[verbose] Completed in {sw.ElapsedMilliseconds}ms");
            }
            catch (RpcException ex)
            {
                Console.Error.WriteLine($"Error: Cannot connect to orchestrator at {addr}. {ex.Status.Detail} ({ex.StatusCode})");
            }
        });

        return command;
    }

    private static Command CreateStatusCommand(Option<string> orchestratorOption)
    {
        var command = new Command("status", "Get ingestion status");

        command.SetAction(async (parseResult, ct) =>
        {
            var addr = parseResult.GetValue(orchestratorOption)!;
            try
            {
                using var clients = new GrpcClientFactory(addr);
                var response = await clients.Orchestrator.GetServicesStatusAsync(
                    new ServicesStatusRequest(), cancellationToken: ct);

                foreach (var svc in response.Services)
                {
                    var lastSync = svc.LastSyncAt?.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm") ?? "never";
                    Console.WriteLine($"{svc.Name}: {svc.Status} (last sync: {lastSync}, items: {svc.ItemCount})");
                }
            }
            catch (RpcException ex)
            {
                Console.Error.WriteLine($"Error: Cannot connect to orchestrator at {addr}. {ex.Status.Detail} ({ex.StatusCode})");
            }
        });

        return command;
    }

    private static Command CreateRebuildCommand(Option<string> orchestratorOption, Option<string> formatOption, Option<bool> verboseOption)
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
            var verbose = parseResult.GetValue(verboseOption);
            try
            {
                var format = parseResult.GetValue(formatOption)!;
                var sources = parseResult.GetValue(sourcesOption);

                var sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
                using var clients = new GrpcClientFactory(addr);
                var request = new TriggerSyncRequest { Type = "rebuild" };

                if (!string.IsNullOrWhiteSpace(sources))
                {
                    foreach (var s in sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        request.Sources.Add(s.ToLowerInvariant());
                }

                var response = await clients.Orchestrator.TriggerSyncAsync(request, cancellationToken: ct);
                OutputFormatter.FormatSyncStatus(response, format);
                if (sw is not null)
                    Console.Error.WriteLine($"[verbose] Completed in {sw.ElapsedMilliseconds}ms");
            }
            catch (RpcException ex)
            {
                Console.Error.WriteLine($"Error: Cannot connect to orchestrator at {addr}. {ex.Status.Detail} ({ex.StatusCode})");
            }
        });

        return command;
    }
}
