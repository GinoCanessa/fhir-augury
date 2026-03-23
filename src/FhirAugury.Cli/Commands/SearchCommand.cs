using System.CommandLine;
using Fhiraugury;
using FhirAugury.Cli.OutputFormatters;
using FhirAugury.Common.Text;
using Grpc.Core;

namespace FhirAugury.Cli.Commands;

public static class SearchCommand
{
    public static Command Create(Option<string> orchestratorOption, Option<string> formatOption, Option<bool> verboseOption)
    {
        var queryArg = new Argument<string>("query")
        {
            Description = "Search query text",
        };
        var sourcesOption = new Option<string?>("--sources")
        {
            Description = "Comma-separated source filter: jira,zulip,confluence,github",
        };
        var limitOption = new Option<int>("--limit")
        {
            Description = "Maximum results to return",
            DefaultValueFactory = _ => 20,
        };

        var command = new Command("search", "Unified search across all FHIR community sources")
        {
            queryArg,
            sourcesOption,
            limitOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var addr = parseResult.GetValue(orchestratorOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            try
            {
                var format = parseResult.GetValue(formatOption)!;
                var query = parseResult.GetValue(queryArg)!;
                var sources = parseResult.GetValue(sourcesOption);
                var limit = parseResult.GetValue(limitOption);

                var sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
                using var clients = new GrpcClientFactory(addr);
                var request = new UnifiedSearchRequest { Query = query, Limit = limit };

                if (!string.IsNullOrWhiteSpace(sources))
                    CsvParser.AddToRepeatedField(request.Sources, sources);

                var response = await clients.Orchestrator.UnifiedSearchAsync(request, cancellationToken: ct);
                OutputFormatter.FormatSearchResults(response, format);
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
