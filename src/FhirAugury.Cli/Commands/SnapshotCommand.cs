using System.CommandLine;
using Fhiraugury;

namespace FhirAugury.Cli.Commands;

public static class SnapshotCommand
{
    public static Command Create(Option<string> orchestratorOption, Option<bool> verboseOption)
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

        var command = new Command("snapshot", "Get a rich markdown snapshot of an item")
        {
            sourceArg,
            idArg,
            includeCommentsOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var addr = parseResult.GetValue(orchestratorOption)!;
            var id = parseResult.GetValue(idArg)!;
            var includeComments = parseResult.GetValue(includeCommentsOption);

            using var clients = new GrpcClientFactory(addr);
            var response = await clients.Orchestrator.GetSnapshotAsync(
                new GetSnapshotRequest { Id = id, IncludeComments = includeComments, IncludeInternalRefs = true },
                cancellationToken: ct);
            Console.WriteLine(response.Markdown);
        });

        return command;
    }
}
