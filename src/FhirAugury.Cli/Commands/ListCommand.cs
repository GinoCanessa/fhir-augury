using System.CommandLine;
using System.Diagnostics;
using Fhiraugury;
using Grpc.Core;

namespace FhirAugury.Cli.Commands;

public static class ListCommand
{
    public static Command Create(Option<string> orchestratorOption, Option<string> formatOption, Option<bool> verboseOption)
    {
        Argument<string> sourceArg = new Argument<string>("source")
        {
            Description = "Source type (jira, zulip, confluence, github)",
        };
        Option<int> limitOption = new Option<int>("--limit")
        {
            Description = "Maximum results",
            DefaultValueFactory = _ => 20,
        };
        Option<string> sortByOption = new Option<string>("--sort-by")
        {
            Description = "Sort by field",
            DefaultValueFactory = _ => "updated_at",
        };
        Option<string> sortOrderOption = new Option<string>("--sort-order")
        {
            Description = "Sort order: asc or desc",
            DefaultValueFactory = _ => "desc",
        };
        Option<string[]> filterOption = new Option<string[]>("--filter")
        {
            Description = "Filters in key=value format (repeatable)",
            AllowMultipleArgumentsPerToken = true,
        };

        Command command = new Command("list", "List items from a source service")
        {
            sourceArg,
            limitOption,
            sortByOption,
            sortOrderOption,
            filterOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string addr = parseResult.GetValue(orchestratorOption)!;
            bool verbose = parseResult.GetValue(verboseOption);
            try
            {
                string format = parseResult.GetValue(formatOption)!;
                string source = parseResult.GetValue(sourceArg)!;
                int limit = parseResult.GetValue(limitOption);
                string sortBy = parseResult.GetValue(sortByOption)!;
                string sortOrder = parseResult.GetValue(sortOrderOption)!;
                string[] filters = parseResult.GetValue(filterOption) ?? [];

                Stopwatch? sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
                using GrpcClientFactory clients = new GrpcClientFactory(addr);

                Dictionary<string, string> endpoints = await clients.GetServiceEndpointsAsync(ct);
                string sourceLower = source.ToLowerInvariant();
                if (!endpoints.TryGetValue(sourceLower, out string? sourceAddress))
                    throw new ArgumentException(
                        $"Unknown or disabled source: {source}. Available: {string.Join(", ", endpoints.Keys)}");
                SourceService.SourceServiceClient sourceClient = clients.GetSourceClient(sourceAddress);

                ListItemsRequest request = new ListItemsRequest
                {
                    Limit = limit,
                    SortBy = sortBy,
                    SortOrder = sortOrder,
                };

                foreach (string? filter in filters)
                {
                    string[] parts = filter.Split('=', 2);
                    if (parts.Length == 2)
                        request.Filters.Add(parts[0].Trim(), parts[1].Trim());
                }

                using AsyncServerStreamingCall<ItemSummary> call = sourceClient.ListItems(request, cancellationToken: ct);

                switch (format.ToLowerInvariant())
                {
                    case "json":
                        List<object> items = new List<object>();
                        await foreach (ItemSummary? item in call.ResponseStream.ReadAllAsync(ct))
                        {
                            items.Add(new
                            {
                                item.Id, item.Title, item.Url,
                                Updated = item.UpdatedAt?.ToDateTimeOffset(),
                                item.Metadata,
                            });
                        }
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(items, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        break;

                    case "markdown":
                    case "md":
                        Console.WriteLine("| ID | Title | Updated | URL |");
                        Console.WriteLine("|----|-------|---------|-----|");
                        await foreach (ItemSummary? item in call.ResponseStream.ReadAllAsync(ct))
                        {
                            string updated = item.UpdatedAt?.ToDateTimeOffset().ToString("yyyy-MM-dd") ?? "";
                            Console.WriteLine($"| {item.Id} | {item.Title} | {updated} | {item.Url} |");
                        }
                        break;

                    default:
                        Console.WriteLine($"{"ID",-16} {"Title",-45} {"Updated",-12} {"URL"}");
                        Console.WriteLine($"{"──────────────",-16} {"───────────────────────────────────────────",-45} {"──────────",-12} {"───"}");
                        int count = 0;
                        await foreach (ItemSummary? item in call.ResponseStream.ReadAllAsync(ct))
                        {
                            string title = item.Title.Length > 43 ? item.Title[..40] + "..." : item.Title;
                            string updated = item.UpdatedAt?.ToDateTimeOffset().ToString("yyyy-MM-dd") ?? "";
                            Console.WriteLine($"{item.Id,-16} {title,-45} {updated,-12} {item.Url}");
                            count++;
                        }
                        Console.WriteLine();
                        Console.WriteLine($"{count} item(s)");
                        break;
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
