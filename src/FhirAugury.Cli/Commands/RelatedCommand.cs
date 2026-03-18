using System.CommandLine;
using FhirAugury.Cli.OutputFormatters;
using FhirAugury.Database;
using FhirAugury.Indexing;

namespace FhirAugury.Cli.Commands;

public static class RelatedCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> verboseOption, Option<bool> jsonOption)
    {
        var command = new Command("related", "Find items related to a given item");

        var sourceOption = new Option<string>("--source") { Description = "Data source: jira, zulip", Arity = ArgumentArity.ExactlyOne };
        var idOption = new Option<string>("--id") { Description = "Item identifier (e.g., FHIR-43499 or stream:topic)", Arity = ArgumentArity.ExactlyOne };
        var limitOption = new Option<int>("--limit", "-n") { Description = "Max results", DefaultValueFactory = _ => 20 };
        var formatOption = new Option<string>("--format", "-f") { Description = "Output format: table | json | markdown", DefaultValueFactory = _ => "table" };

        command.Add(sourceOption);
        command.Add(idOption);
        command.Add(limitOption);
        command.Add(formatOption);

        command.SetAction((parseResult, _) =>
        {
            var source = parseResult.GetValue(sourceOption)!;
            var id = parseResult.GetValue(idOption)!;
            var dbPath = parseResult.GetValue(dbOption)!;
            var limit = parseResult.GetValue(limitOption);
            var format = parseResult.GetValue(jsonOption) ? "json" : parseResult.GetValue(formatOption)!;

            var dbService = new DatabaseService(dbPath);
            dbService.InitializeDatabase();

            using var conn = dbService.OpenConnection();

            var results = SimilaritySearchService.FindRelated(conn, source, id, limit);

            if (results.Count == 0)
            {
                Console.WriteLine("No related items found.");
                return Task.CompletedTask;
            }

            OutputFormatter.Format(results, format);
            return Task.CompletedTask;
        });

        return command;
    }
}
