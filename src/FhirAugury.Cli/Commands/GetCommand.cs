using System.CommandLine;
using System.Diagnostics;
using Fhiraugury;
using FhirAugury.Cli.OutputFormatters;
using Grpc.Core;

namespace FhirAugury.Cli.Commands;

public static class GetCommand
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
        Option<bool> includeCommentsOption = new Option<bool>("--comments")
        {
            Description = "Include comments",
            DefaultValueFactory = _ => true,
        };

        Command command = new Command("get", "Get full details of an item")
        {
            sourceArg,
            idArg,
            includeCommentsOption,
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
                bool includeComments = parseResult.GetValue(includeCommentsOption);

                Stopwatch? sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
                using GrpcClientFactory clients = new GrpcClientFactory(addr);
                Metadata headers = new Metadata { { "x-source", source } };
                ItemResponse response = await clients.Orchestrator.GetItemAsync(
                    new GetItemRequest { Id = id, IncludeContent = true, IncludeComments = includeComments, SourceName = source },
                    headers: headers,
                    cancellationToken: ct);
                OutputFormatter.FormatItem(response, format);
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
