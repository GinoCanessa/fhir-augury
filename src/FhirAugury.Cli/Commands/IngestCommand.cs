using System.CommandLine;
using System.Diagnostics;
using Fhiraugury;
using FhirAugury.Cli.OutputFormatters;
using FhirAugury.Common.Text;
using Grpc.Core;

namespace FhirAugury.Cli.Commands;

public static class IngestCommand
{
    public static Command Create(Option<string> orchestratorOption, Option<string> formatOption, Option<bool> verboseOption)
    {
        Command command = new Command("ingest", "Ingestion and sync management");

        command.Add(CreateTriggerCommand(orchestratorOption, formatOption, verboseOption));
        command.Add(CreateStatusCommand(orchestratorOption));
        command.Add(CreateRebuildCommand(orchestratorOption, formatOption, verboseOption));
        command.Add(CreateIndexCommand(orchestratorOption, formatOption, verboseOption));

        return command;
    }

    private static Command CreateTriggerCommand(Option<string> orchestratorOption, Option<string> formatOption, Option<bool> verboseOption)
    {
        Option<string?> sourcesOption = new Option<string?>("--sources")
        {
            Description = "Comma-separated sources to sync (empty for all)",
        };
        Option<string> typeOption = new Option<string>("--type")
        {
            Description = "Sync type: incremental, full, rebuild",
            DefaultValueFactory = _ => "incremental",
        };

        Command command = new Command("trigger", "Trigger synchronization across source services")
        {
            sourcesOption,
            typeOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string addr = parseResult.GetValue(orchestratorOption)!;
            bool verbose = parseResult.GetValue(verboseOption);
            try
            {
                string format = parseResult.GetValue(formatOption)!;
                string? sources = parseResult.GetValue(sourcesOption);
                string type = parseResult.GetValue(typeOption)!;

                Stopwatch? sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
                using GrpcClientFactory clients = new GrpcClientFactory(addr);
                TriggerSyncRequest request = new TriggerSyncRequest { Type = type };

                if (!string.IsNullOrWhiteSpace(sources))
                {
                    foreach (string s in sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        request.Sources.Add(s.ToLowerInvariant());
                }

                TriggerSyncResponse response = await clients.Orchestrator.TriggerSyncAsync(request, cancellationToken: ct);
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
        Command command = new Command("status", "Get ingestion status");

        command.SetAction(async (parseResult, ct) =>
        {
            string addr = parseResult.GetValue(orchestratorOption)!;
            try
            {
                using GrpcClientFactory clients = new GrpcClientFactory(addr);
                ServicesStatusResponse response = await clients.Orchestrator.GetServicesStatusAsync(
                    new ServicesStatusRequest(), cancellationToken: ct);

                foreach (ServiceHealth? svc in response.Services)
                {
                    string lastSync = svc.LastSyncAt?.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm") ?? "never";
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
        Option<string?> sourcesOption = new Option<string?>("--sources")
        {
            Description = "Comma-separated sources to rebuild (empty for all)",
        };

        Command command = new Command("rebuild", "Rebuild from cache")
        {
            sourcesOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string addr = parseResult.GetValue(orchestratorOption)!;
            bool verbose = parseResult.GetValue(verboseOption);
            try
            {
                string format = parseResult.GetValue(formatOption)!;
                string? sources = parseResult.GetValue(sourcesOption);

                Stopwatch? sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
                using GrpcClientFactory clients = new GrpcClientFactory(addr);
                TriggerSyncRequest request = new TriggerSyncRequest { Type = "rebuild" };

                if (!string.IsNullOrWhiteSpace(sources))
                    CsvParser.AddToRepeatedField(request.Sources, sources);

                TriggerSyncResponse response = await clients.Orchestrator.TriggerSyncAsync(request, cancellationToken: ct);
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

    private static Command CreateIndexCommand(Option<string> orchestratorOption, Option<string> formatOption, Option<bool> verboseOption)
    {
        Option<string?> sourcesOption = new Option<string?>("--sources")
        {
            Description = "Comma-separated sources to rebuild indexes on (empty for all)",
        };
        Option<string> typeOption = new Option<string>("--type")
        {
            Description = "Index type: all, bm25, fts, cross-refs, lookup-tables, commits, artifact-map, page-links",
            DefaultValueFactory = _ => "all",
        };

        Command command = new Command("index", "Rebuild specific indexes on source services")
        {
            sourcesOption,
            typeOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string addr = parseResult.GetValue(orchestratorOption)!;
            bool verbose = parseResult.GetValue(verboseOption);
            try
            {
                string? sources = parseResult.GetValue(sourcesOption);
                string type = parseResult.GetValue(typeOption)!;

                Stopwatch? sw = verbose ? Stopwatch.StartNew() : null;
                using GrpcClientFactory clients = new GrpcClientFactory(addr);
                OrchestratorRebuildIndexRequest request = new() { IndexType = type };

                if (!string.IsNullOrWhiteSpace(sources))
                    CsvParser.AddToRepeatedField(request.Sources, sources);

                OrchestratorRebuildIndexResponse response =
                    await clients.Orchestrator.RebuildIndexAsync(request, cancellationToken: ct);

                foreach (SourceRebuildIndexStatus status in response.Results)
                {
                    string icon = status.Success ? "✓" : "✗";
                    string detail = !string.IsNullOrEmpty(status.ActionTaken) ? status.ActionTaken : status.Error;
                    Console.WriteLine($"  {icon} {status.Source}: {detail}");
                }

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
