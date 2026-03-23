using System.CommandLine;
using Fhiraugury;
using FhirAugury.Cli.OutputFormatters;
using Grpc.Core;

namespace FhirAugury.Cli.Commands;

public static class GetCommand
{
    public static Command Create(Option<string> orchestratorOption, Option<string> formatOption, Option<bool> verboseOption)
    {
        var sourceArg = new Argument<string>("source")
        {
            Description = "Source type (jira, zulip, confluence, github)",
        };
        var idArg = new Argument<string>("id")
        {
            Description = "Item identifier",
        };
        var includeCommentsOption = new Option<bool>("--comments")
        {
            Description = "Include comments",
            DefaultValueFactory = _ => true,
        };

        var command = new Command("get", "Get full details of an item")
        {
            sourceArg,
            idArg,
            includeCommentsOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var addr = parseResult.GetValue(orchestratorOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            try
            {
                var format = parseResult.GetValue(formatOption)!;
                var source = parseResult.GetValue(sourceArg)!;
                var id = parseResult.GetValue(idArg)!;
                var includeComments = parseResult.GetValue(includeCommentsOption);

                var sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
                using var clients = new GrpcClientFactory(addr);
                var headers = new Metadata { { "x-source", source } };
                var response = await clients.Orchestrator.GetItemAsync(
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
