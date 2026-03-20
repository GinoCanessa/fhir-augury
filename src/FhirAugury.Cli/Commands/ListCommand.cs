using System.CommandLine;
using Fhiraugury;
using Grpc.Core;

namespace FhirAugury.Cli.Commands;

public static class ListCommand
{
    public static Command Create(Option<string> orchestratorOption, Option<string> formatOption, Option<bool> verboseOption)
    {
        var sourceArg = new Argument<string>("source")
        {
            Description = "Source type (jira, zulip, confluence, github)",
        };
        var limitOption = new Option<int>("--limit")
        {
            Description = "Maximum results",
            DefaultValueFactory = _ => 20,
        };
        var sortByOption = new Option<string>("--sort-by")
        {
            Description = "Sort by field",
            DefaultValueFactory = _ => "updated_at",
        };
        var sortOrderOption = new Option<string>("--sort-order")
        {
            Description = "Sort order: asc or desc",
            DefaultValueFactory = _ => "desc",
        };
        var filterOption = new Option<string[]>("--filter")
        {
            Description = "Filters in key=value format (repeatable)",
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command("list", "List items from a source service")
        {
            sourceArg,
            limitOption,
            sortByOption,
            sortOrderOption,
            filterOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var addr = parseResult.GetValue(orchestratorOption)!;
            var format = parseResult.GetValue(formatOption)!;
            var source = parseResult.GetValue(sourceArg)!;
            var limit = parseResult.GetValue(limitOption);
            var sortBy = parseResult.GetValue(sortByOption)!;
            var sortOrder = parseResult.GetValue(sortOrderOption)!;
            var filters = parseResult.GetValue(filterOption) ?? [];

            using var clients = new GrpcClientFactory(addr);

            var sourceClient = source.ToLowerInvariant() switch
            {
                "jira" => clients.JiraSource,
                "zulip" => clients.ZulipSource,
                _ => throw new ArgumentException($"Unknown source: {source}. Supported: jira, zulip"),
            };

            var request = new ListItemsRequest
            {
                Limit = limit,
                SortBy = sortBy,
                SortOrder = sortOrder,
            };

            foreach (var filter in filters)
            {
                var parts = filter.Split('=', 2);
                if (parts.Length == 2)
                    request.Filters.Add(parts[0].Trim(), parts[1].Trim());
            }

            using var call = sourceClient.ListItems(request, cancellationToken: ct);

            switch (format.ToLowerInvariant())
            {
                case "json":
                    var items = new List<object>();
                    await foreach (var item in call.ResponseStream.ReadAllAsync(ct))
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
                    await foreach (var item in call.ResponseStream.ReadAllAsync(ct))
                    {
                        var updated = item.UpdatedAt?.ToDateTimeOffset().ToString("yyyy-MM-dd") ?? "";
                        Console.WriteLine($"| {item.Id} | {item.Title} | {updated} | {item.Url} |");
                    }
                    break;

                default:
                    Console.WriteLine($"{"ID",-16} {"Title",-45} {"Updated",-12} {"URL"}");
                    Console.WriteLine($"{"──────────────",-16} {"───────────────────────────────────────────",-45} {"──────────",-12} {"───"}");
                    var count = 0;
                    await foreach (var item in call.ResponseStream.ReadAllAsync(ct))
                    {
                        var title = item.Title.Length > 43 ? item.Title[..40] + "..." : item.Title;
                        var updated = item.UpdatedAt?.ToDateTimeOffset().ToString("yyyy-MM-dd") ?? "";
                        Console.WriteLine($"{item.Id,-16} {title,-45} {updated,-12} {item.Url}");
                        count++;
                    }
                    Console.WriteLine();
                    Console.WriteLine($"{count} item(s)");
                    break;
            }
        });

        return command;
    }
}
