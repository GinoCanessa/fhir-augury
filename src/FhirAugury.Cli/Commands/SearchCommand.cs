using System.CommandLine;
using System.Diagnostics;
using Fhiraugury;
using FhirAugury.Cli.OutputFormatters;
using FhirAugury.Common.Text;
using Grpc.Core;

namespace FhirAugury.Cli.Commands;

public static class SearchCommand
{
    public static Command Create(Option<string> orchestratorOption, Option<string> formatOption, Option<bool> verboseOption)
    {
        Argument<string> queryArg = new Argument<string>("query")
        {
            Description = "Search query text",
        };
        Option<string?> sourcesOption = new Option<string?>("--sources")
        {
            Description = "Comma-separated source filter: jira,zulip,confluence,github",
        };
        Option<int> limitOption = new Option<int>("--limit")
        {
            Description = "Maximum results to return",
            DefaultValueFactory = _ => 20,
        };

        Command command = new Command("search", "Unified search across all FHIR community sources")
        {
            queryArg,
            sourcesOption,
            limitOption,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            string addr = parseResult.GetValue(orchestratorOption)!;
            bool verbose = parseResult.GetValue(verboseOption);
            try
            {
                string format = parseResult.GetValue(formatOption)!;
                string query = parseResult.GetValue(queryArg)!;
                string? sources = parseResult.GetValue(sourcesOption);
                int limit = parseResult.GetValue(limitOption);

                Stopwatch? sw = verbose ? System.Diagnostics.Stopwatch.StartNew() : null;
                using GrpcClientFactory clients = new GrpcClientFactory(addr);
                UnifiedSearchRequest request = new UnifiedSearchRequest { Query = query, Limit = limit };

                if (!string.IsNullOrWhiteSpace(sources))
                    CsvParser.AddToRepeatedField(request.Sources, sources);

                SearchResponse response = await clients.Orchestrator.UnifiedSearchAsync(request, cancellationToken: ct);
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
