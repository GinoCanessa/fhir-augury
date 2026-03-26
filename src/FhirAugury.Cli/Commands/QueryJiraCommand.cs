using System.CommandLine;
using System.Diagnostics;
using Fhiraugury;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace FhirAugury.Cli.Commands;

public static class QueryJiraCommand
{
    public static Command Create(Option<string> orchestratorOption, Option<string> formatOption, Option<bool> verboseOption)
    {
        Option<string?> statusesOption = new Option<string?>("--statuses")
        {
            Description = "Filter by statuses (comma-separated)",
        };
        Option<string?> workGroupsOption = new Option<string?>("--work-groups")
        {
            Description = "Filter by work groups (comma-separated)",
        };
        Option<string?> specificationsOption = new Option<string?>("--specs")
        {
            Description = "Filter by specifications (comma-separated)",
        };
        Option<string?> typesOption = new Option<string?>("--types")
        {
            Description = "Filter by issue types (comma-separated)",
        };
        Option<string?> prioritiesOption = new Option<string?>("--priorities")
        {
            Description = "Filter by priorities (comma-separated)",
        };
        Option<string?> labelsOption = new Option<string?>("--labels")
        {
            Description = "Filter by labels (comma-separated)",
        };
        Option<string?> assigneesOption = new Option<string?>("--assignees")
        {
            Description = "Filter by assignees (comma-separated)",
        };
        Option<string?> queryOption = new Option<string?>("--query")
        {
            Description = "Text query for additional filtering",
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
        Option<int> limitOption = new Option<int>("--limit")
        {
            Description = "Maximum results",
            DefaultValueFactory = _ => 20,
        };
        Option<DateTimeOffset?> updatedAfterOption = new Option<DateTimeOffset?>("--updated-after")
        {
            Description = "Only issues updated after this date",
        };

        Command command = new Command("query-jira", "Query Jira issues with structured filters")
        {
            statusesOption,
            workGroupsOption,
            specificationsOption,
            typesOption,
            prioritiesOption,
            labelsOption,
            assigneesOption,
            queryOption,
            sortByOption,
            sortOrderOption,
            limitOption,
            updatedAfterOption,
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
                JiraQueryRequest request = new JiraQueryRequest
                {
                    Query = parseResult.GetValue(queryOption) ?? "",
                    SortBy = parseResult.GetValue(sortByOption)!,
                    SortOrder = parseResult.GetValue(sortOrderOption)!,
                    Limit = parseResult.GetValue(limitOption),
                };

                AddItems(request.Statuses, parseResult.GetValue(statusesOption));
                AddItems(request.WorkGroups, parseResult.GetValue(workGroupsOption));
                AddItems(request.Specifications, parseResult.GetValue(specificationsOption));
                AddItems(request.Types_, parseResult.GetValue(typesOption));
                AddItems(request.Priorities, parseResult.GetValue(prioritiesOption));
                AddItems(request.Labels, parseResult.GetValue(labelsOption));
                AddItems(request.Assignees, parseResult.GetValue(assigneesOption));

                DateTimeOffset? updatedAfter = parseResult.GetValue(updatedAfterOption);
                if (updatedAfter.HasValue)
                    request.UpdatedAfter = Timestamp.FromDateTimeOffset(updatedAfter.Value);

                using AsyncServerStreamingCall<JiraIssueSummary> call = clients.Jira.QueryIssues(request, cancellationToken: ct);

                switch (format.ToLowerInvariant())
                {
                    case "json":
                        List<object> items = new List<object>();
                        await foreach (JiraIssueSummary? issue in call.ResponseStream.ReadAllAsync(ct))
                        {
                            items.Add(new
                            {
                                issue.Key, issue.ProjectKey, issue.Title, issue.Type,
                                issue.Status, issue.Priority, issue.WorkGroup, issue.Specification,
                                Updated = issue.UpdatedAt?.ToDateTimeOffset(),
                            });
                        }
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(items, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        break;

                    case "markdown":
                    case "md":
                        Console.WriteLine("| Key | Status | Type | Title | Work Group | Updated |");
                        Console.WriteLine("|-----|--------|------|-------|------------|---------|");
                        await foreach (JiraIssueSummary? issue in call.ResponseStream.ReadAllAsync(ct))
                        {
                            string updated = issue.UpdatedAt?.ToDateTimeOffset().ToString("yyyy-MM-dd") ?? "";
                            Console.WriteLine($"| {issue.Key} | {issue.Status} | {issue.Type} | {issue.Title} | {issue.WorkGroup} | {updated} |");
                        }
                        break;

                    default:
                        Console.WriteLine($"{"Key",-14} {"Status",-12} {"Type",-12} {"Title",-40} {"Work Group",-20} {"Updated",-12}");
                        Console.WriteLine($"{"────────────",-14} {"──────────",-12} {"──────────",-12} {"──────────────────────────────────────",-40} {"──────────────────",-20} {"──────────",-12}");
                        int count = 0;
                        await foreach (JiraIssueSummary? issue in call.ResponseStream.ReadAllAsync(ct))
                        {
                            string title = issue.Title.Length > 38 ? issue.Title[..35] + "..." : issue.Title;
                            string updated = issue.UpdatedAt?.ToDateTimeOffset().ToString("yyyy-MM-dd") ?? "";
                            Console.WriteLine($"{issue.Key,-14} {issue.Status,-12} {issue.Type,-12} {title,-40} {issue.WorkGroup,-20} {updated,-12}");
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
