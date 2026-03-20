using System.CommandLine;
using Fhiraugury;
using FhirAugury.Cli.OutputFormatters;

namespace FhirAugury.Cli.Commands;

public static class XrefCommand
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
        var directionOption = new Option<string>("--direction")
        {
            Description = "Direction: outgoing, incoming, or both",
            DefaultValueFactory = _ => "both",
        };

        var command = new Command("xref", "Get cross-references for a specific item")
        {
            sourceArg,
            idArg,
            directionOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var addr = parseResult.GetValue(orchestratorOption)!;
            var format = parseResult.GetValue(formatOption)!;
            var source = parseResult.GetValue(sourceArg)!;
            var id = parseResult.GetValue(idArg)!;
            var direction = parseResult.GetValue(directionOption)!;

            using var clients = new GrpcClientFactory(addr);
            var response = await clients.Orchestrator.GetCrossReferencesAsync(
                new GetXRefRequest { Source = source, Id = id, Direction = direction },
                cancellationToken: ct);
            OutputFormatter.FormatCrossReferences(response, source, id, format);
        });

        return command;
    }
}
