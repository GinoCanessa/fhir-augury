using System.CommandLine;
using System.Diagnostics;
using Fhiraugury;
using FhirAugury.Cli.OutputFormatters;
using FhirAugury.Common.Text;
using Grpc.Core;

namespace FhirAugury.Cli.Commands;

public static class RelatedCommand
{
    public static Command Create(Option<string> orchestratorOption, Option<string> formatOption, Option<bool> verboseOption)
    {
        Argument<string> sourceArg = new Argument<string>("source")
        {
            Description = "Source type (jira, zulip, confluence, github)",
        };
        Argument<string> idArg = new Argument<string>("id")
        {
            Description = "Item identifier",
        };
        Option<int> limitOption = new Option<int>("--limit")
        {
            Description = "Maximum results",
            DefaultValueFactory = _ => 20,
        };
        Option<string?> targetSourcesOption = new Option<string?>("--target-sources")
        {
            Description = "Comma-separated target sources to search",
        };

        Command command = new Command("related", "Find items across all sources related to a given item")
        {
            sourceArg,
            idArg,
            limitOption,
            targetSourcesOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string addr = parseResult.GetValue(orchestratorOption)!;
            bool verbose = parseResult.GetValue(verboseOption);
            try
            {
                string format = parseResult.GetValue(formatOption)!;
                string source = parseResult.GetValue(sourceArg)!;
                string id = parseResult.GetValue(idArg)!;
                int limit = parseResult.GetValue(limitOption);
                string? targetSources = parseResult.GetValue(targetSourcesOption);

                Stopwatch? sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
                using GrpcClientFactory clients = new GrpcClientFactory(addr);
                FindRelatedRequest request = new FindRelatedRequest { Source = source, Id = id, Limit = limit };

                if (!string.IsNullOrWhiteSpace(targetSources))
                    CsvParser.AddToRepeatedField(request.TargetSources, targetSources);

                FindRelatedResponse response = await clients.Orchestrator.FindRelatedAsync(request, cancellationToken: ct);
                OutputFormatter.FormatRelated(response, format);
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
