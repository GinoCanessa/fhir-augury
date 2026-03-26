using System.CommandLine;
using System.Diagnostics;
using Fhiraugury;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace FhirAugury.Cli.Commands;

public static class QueryZulipCommand
{
    public static Command Create(Option<string> orchestratorOption, Option<string> formatOption, Option<bool> verboseOption)
    {
        Option<string?> streamsOption = new Option<string?>("--streams")
        {
            Description = "Filter by stream names (comma-separated)",
        };
        Option<string?> topicOption = new Option<string?>("--topic")
        {
            Description = "Filter by exact topic name",
        };
        Option<string?> topicKeywordOption = new Option<string?>("--topic-keyword")
        {
            Description = "Filter by topic keyword (partial match)",
        };
        Option<string?> sendersOption = new Option<string?>("--senders")
        {
            Description = "Filter by sender names (comma-separated)",
        };
        Option<string?> queryOption = new Option<string?>("--query")
        {
            Description = "Text query",
        };
        Option<string> sortByOption = new Option<string>("--sort-by")
        {
            Description = "Sort by field",
            DefaultValueFactory = _ => "timestamp",
        };
        Option<string> sortOrderOption = new Option<string>("--sort-order")
        {
            Description = "Sort order: asc or desc",
            DefaultValueFactory = _ => "desc",
        };
        Option<int> limitOption = new Option<int>("--limit")
        {
            Description = "Maximum results",
            DefaultValueFactory = _ => 20,
        };
        Option<DateTimeOffset?> afterOption = new Option<DateTimeOffset?>("--after")
        {
            Description = "Only messages after this date",
        };
        Option<DateTimeOffset?> beforeOption = new Option<DateTimeOffset?>("--before")
        {
            Description = "Only messages before this date",
        };

        Command command = new Command("query-zulip", "Query Zulip messages with structured filters")
        {
            streamsOption,
            topicOption,
            topicKeywordOption,
            sendersOption,
            queryOption,
            sortByOption,
            sortOrderOption,
            limitOption,
            afterOption,
            beforeOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string addr = parseResult.GetValue(orchestratorOption)!;
            bool verbose = parseResult.GetValue(verboseOption);
            try
            {
                string format = parseResult.GetValue(formatOption)!;

                Stopwatch? sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
                using GrpcClientFactory clients = new GrpcClientFactory(addr);
                ZulipQueryRequest request = new ZulipQueryRequest
                {
                    Topic = parseResult.GetValue(topicOption) ?? "",
                    TopicKeyword = parseResult.GetValue(topicKeywordOption) ?? "",
                    Query = parseResult.GetValue(queryOption) ?? "",
                    SortBy = parseResult.GetValue(sortByOption)!,
                    SortOrder = parseResult.GetValue(sortOrderOption)!,
                    Limit = parseResult.GetValue(limitOption),
                };

                AddItems(request.StreamNames, parseResult.GetValue(streamsOption));
                AddItems(request.SenderNames, parseResult.GetValue(sendersOption));

                DateTimeOffset? after = parseResult.GetValue(afterOption);
                if (after.HasValue)
                    request.After = Timestamp.FromDateTimeOffset(after.Value);
                DateTimeOffset? before = parseResult.GetValue(beforeOption);
                if (before.HasValue)
                    request.Before = Timestamp.FromDateTimeOffset(before.Value);

                using AsyncServerStreamingCall<ZulipMessageSummary> call = clients.Zulip.QueryMessages(request, cancellationToken: ct);

                switch (format.ToLowerInvariant())
                {
                    case "json":
                        List<object> items = new List<object>();
                        await foreach (ZulipMessageSummary? msg in call.ResponseStream.ReadAllAsync(ct))
                        {
                            items.Add(new
                            {
                                msg.Id, msg.StreamName, msg.Topic, msg.SenderName,
                                msg.Snippet, Timestamp = msg.Timestamp?.ToDateTimeOffset(),
                            });
                        }
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(items, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        break;

                    case "markdown":
                    case "md":
                        Console.WriteLine("| Stream | Topic | Sender | Snippet | Timestamp |");
                        Console.WriteLine("|--------|-------|--------|---------|-----------|");
                        await foreach (ZulipMessageSummary? msg in call.ResponseStream.ReadAllAsync(ct))
                        {
                            string ts = msg.Timestamp?.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm") ?? "";
                            string snippet = msg.Snippet.Length > 50 ? msg.Snippet[..47] + "..." : msg.Snippet;
                            Console.WriteLine($"| {msg.StreamName} | {msg.Topic} | {msg.SenderName} | {snippet} | {ts} |");
                        }
                        break;

                    default:
                        Console.WriteLine($"{"Stream",-20} {"Topic",-25} {"Sender",-16} {"Snippet",-40} {"Timestamp",-18}");
                        Console.WriteLine($"{"──────────────────",-20} {"───────────────────────",-25} {"──────────────",-16} {"──────────────────────────────────────",-40} {"────────────────",-18}");
                        int count = 0;
                        await foreach (ZulipMessageSummary? msg in call.ResponseStream.ReadAllAsync(ct))
                        {
                            string snippet = msg.Snippet.Length > 38 ? msg.Snippet[..35] + "..." : msg.Snippet;
                            string ts = msg.Timestamp?.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm") ?? "";
                            Console.WriteLine($"{msg.StreamName,-20} {msg.Topic,-25} {msg.SenderName,-16} {snippet,-40} {ts,-18}");
                            count++;
                        }
                        Console.WriteLine();
                        Console.WriteLine($"{count} result(s)");
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

    private static void AddItems(Google.Protobuf.Collections.RepeatedField<string> field, string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return;
        foreach (string item in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            field.Add(item);
    }
}
