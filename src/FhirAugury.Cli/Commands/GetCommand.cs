using System.CommandLine;
using FhirAugury.Cli.OutputFormatters;
using FhirAugury.Database;
using FhirAugury.Database.Records;

namespace FhirAugury.Cli.Commands;

public static class GetCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> verboseOption, Option<bool> jsonOption)
    {
        var command = new Command("get", "Retrieve a specific item by identifier");

        var sourceOption = new Option<string>("--source") { Description = "Data source: jira", Arity = ArgumentArity.ExactlyOne };
        var idOption = new Option<string>("--id") { Description = "Item identifier (e.g., FHIR-43499)", Arity = ArgumentArity.ExactlyOne };
        var formatOption = new Option<string>("--format", "-f") { Description = "Output format: table | json | markdown", DefaultValueFactory = _ => "table" };

        command.Add(sourceOption);
        command.Add(idOption);
        command.Add(formatOption);

        command.SetAction((parseResult, _) =>
        {
            var source = parseResult.GetValue(sourceOption)!;
            var id = parseResult.GetValue(idOption)!;
            var dbPath = parseResult.GetValue(dbOption)!;
            var format = parseResult.GetValue(jsonOption) ? "json" : parseResult.GetValue(formatOption)!;

            if (source != "jira")
            {
                Console.Error.WriteLine($"Source '{source}' is not supported in Phase 1.");
                return Task.CompletedTask;
            }

            var dbService = new DatabaseService(dbPath);
            dbService.InitializeDatabase();

            using var conn = dbService.OpenConnection();
            var issue = JiraIssueRecord.SelectSingle(conn, Key: id);

            if (issue is null)
            {
                Console.Error.WriteLine($"Issue '{id}' not found.");
                return Task.CompletedTask;
            }

            var comments = JiraCommentRecord.SelectList(conn, IssueKey: id);
            OutputFormatter.FormatJiraIssue(issue, comments, format);
            return Task.CompletedTask;
        });

        return command;
    }
}
