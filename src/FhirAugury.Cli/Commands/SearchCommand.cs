using System.CommandLine;
using FhirAugury.Cli.OutputFormatters;
using FhirAugury.Database;
using FhirAugury.Indexing;
using FhirAugury.Models;

namespace FhirAugury.Cli.Commands;

public static class SearchCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> verboseOption, Option<bool> jsonOption)
    {
        var command = new Command("search", "Search the knowledge base");

        var queryOption = new Option<string>("--query", "-q") { Description = "Search query text", Arity = ArgumentArity.ExactlyOne };
        var sourceFilterOption = new Option<string?>("--source", "-s") { Description = "Filter to source(s): jira, zulip, jira-comment" };
        var limitOption = new Option<int>("--limit", "-n") { Description = "Max results", DefaultValueFactory = _ => 20 };
        var formatOption = new Option<string>("--format", "-f") { Description = "Output format: table | json | markdown", DefaultValueFactory = _ => "table" };

        command.Add(queryOption);
        command.Add(sourceFilterOption);
        command.Add(limitOption);
        command.Add(formatOption);

        command.SetAction((parseResult, _) =>
        {
            var query = parseResult.GetValue(queryOption)!;
            var dbPath = parseResult.GetValue(dbOption)!;
            var limit = parseResult.GetValue(limitOption);
            var format = parseResult.GetValue(jsonOption) ? "json" : parseResult.GetValue(formatOption)!;
            var sourceFilter = parseResult.GetValue(sourceFilterOption);

            var dbService = new DatabaseService(dbPath);
            dbService.InitializeDatabase();

            using var conn = dbService.OpenConnection();

            HashSet<string>? sources = null;
            if (!string.IsNullOrEmpty(sourceFilter))
            {
                sources = [.. sourceFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            }

            var results = FtsSearchService.SearchAll(conn, query, sources, limit);

            if (results.Count == 0)
            {
                Console.WriteLine("No results found.");
                return Task.CompletedTask;
            }

            OutputFormatter.Format(results, format);
            return Task.CompletedTask;
        });

        return command;
    }
}
