using System.CommandLine;
using Fhiraugury;
using FhirAugury.Cli.OutputFormatters;
using Grpc.Core;

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
            try
            {
                var format = parseResult.GetValue(formatOption)!;

                using var clients = new GrpcClientFactory(addr);
                var response = await clients.Orchestrator.GetServicesStatusAsync(
                    new ServicesStatusRequest(), cancellationToken: ct);
                OutputFormatter.FormatServicesStatus(response, format);
            }
            catch (RpcException ex)
            {
                Console.Error.WriteLine($"Error: Cannot connect to orchestrator at {addr}. {ex.Status.Detail} ({ex.StatusCode})");
            }
        });

        return command;
    }

    private static Command CreateStatsCommand(Option<string> orchestratorOption, Option<string> formatOption)
    {
        var command = new Command("stats", "Get aggregate statistics across all services");

        command.SetAction(async (parseResult, ct) =>
        {
            var addr = parseResult.GetValue(orchestratorOption)!;
            try
            {
                var format = parseResult.GetValue(formatOption)!;

                using var clients = new GrpcClientFactory(addr);

                // Get status for overall info
                var statusResponse = await clients.Orchestrator.GetServicesStatusAsync(
                    new ServicesStatusRequest(), cancellationToken: ct);

                // Get per-source stats via SourceService.GetStats
                var statsResults = new List<StatsResponse>();
                foreach (var svc in statusResponse.Services)
                {
                    try
                    {
                        var sourceClient = new SourceService.SourceServiceClient(
                            Grpc.Net.Client.GrpcChannel.ForAddress(svc.GrpcAddress));
                        var stats = await sourceClient.GetStatsAsync(new StatsRequest(), cancellationToken: ct);
                        statsResults.Add(stats);
                    }
                    catch
                    {
                        // Service may be unreachable
                    }
                }

                switch (format.ToLowerInvariant())
                {
                    case "json":
                        var jsonObj = new
                        {
                            crossRefLinks = statusResponse.CrossRefLinks,
                            lastXrefScan = statusResponse.LastXrefScanAt?.ToDateTimeOffset(),
                            sources = statsResults.Select(s => new
                            {
                                s.Source, s.TotalItems, s.TotalComments,
                                s.DatabaseSizeBytes, s.CacheSizeBytes,
                                LastSync = s.LastSyncAt?.ToDateTimeOffset(),
                                OldestItem = s.OldestItem?.ToDateTimeOffset(),
                                NewestItem = s.NewestItem?.ToDateTimeOffset(),
                                AdditionalCounts = new Dictionary<string, int>(s.AdditionalCounts),
                            }),
                        };
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(jsonObj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        break;
                    default:
                        Console.WriteLine($"Cross-Reference Links: {statusResponse.CrossRefLinks}");
                        if (statusResponse.LastXrefScanAt is not null)
                            Console.WriteLine($"Last XRef Scan:        {statusResponse.LastXrefScanAt.ToDateTimeOffset():yyyy-MM-dd HH:mm}");
                        Console.WriteLine();
                        Console.WriteLine($"{"Source",-12} {"Items",8} {"Comments",10} {"DB Size",10} {"Cache",10} {"Last Sync",-20}");
                        Console.WriteLine($"{"─────────",-12} {"──────",8} {"────────",10} {"────────",10} {"────────",10} {"──────────────────",-20}");
                        foreach (var s in statsResults)
                        {
                            var dbSize = OutputFormatter.FormatBytes(s.DatabaseSizeBytes);
                            var cacheSize = OutputFormatter.FormatBytes(s.CacheSizeBytes);
                            var lastSync = s.LastSyncAt?.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm") ?? "never";
                            Console.WriteLine($"{s.Source,-12} {s.TotalItems,8} {s.TotalComments,10} {dbSize,10} {cacheSize,10} {lastSync,-20}");
                        }
                        break;
                }
            }
            catch (RpcException ex)
            {
                Console.Error.WriteLine($"Error: Cannot connect to orchestrator at {addr}. {ex.Status.Detail} ({ex.StatusCode})");
            }
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
            var verbose = parseResult.GetValue(verboseOption);
            try
            {
                var fullRescan = parseResult.GetValue(fullRescanOption);

                var sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
                using var clients = new GrpcClientFactory(addr);
                var response = await clients.Orchestrator.TriggerXRefScanAsync(
                    new TriggerXRefScanRequest { FullRescan = fullRescan },
                    cancellationToken: ct);

                Console.WriteLine($"XRef scan: {response.Status} ({response.ItemsToScan} items to scan)");
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
