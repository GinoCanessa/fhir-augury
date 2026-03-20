using System.CommandLine;
using Fhiraugury;
using FhirAugury.Cli.OutputFormatters;

namespace FhirAugury.Cli.Commands;

public static class RelatedCommand
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
        var limitOption = new Option<int>("--limit")
        {
            Description = "Maximum results",
            DefaultValueFactory = _ => 20,
        };
        var targetSourcesOption = new Option<string?>("--target-sources")
        {
            Description = "Comma-separated target sources to search",
        };

        var command = new Command("related", "Find items across all sources related to a given item")
        {
            sourceArg,
            idArg,
            limitOption,
            targetSourcesOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var addr = parseResult.GetValue(orchestratorOption)!;
            var format = parseResult.GetValue(formatOption)!;
            var source = parseResult.GetValue(sourceArg)!;
            var id = parseResult.GetValue(idArg)!;
            var limit = parseResult.GetValue(limitOption);
            var targetSources = parseResult.GetValue(targetSourcesOption);

            using var clients = new GrpcClientFactory(addr);
            var request = new FindRelatedRequest { Source = source, Id = id, Limit = limit };

            if (!string.IsNullOrWhiteSpace(targetSources))
            {
                foreach (var s in targetSources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    request.TargetSources.Add(s.ToLowerInvariant());
            }

            var response = await clients.Orchestrator.FindRelatedAsync(request, cancellationToken: ct);
            OutputFormatter.FormatRelated(response, format);
        });

        return command;
    }
}
