using System.CommandLine;
using FhirAugury.Database;
using FhirAugury.Models;
using FhirAugury.Sources.Jira;

namespace FhirAugury.Cli.Commands;

public static class IngestCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> verboseOption, Option<bool> jsonOption)
    {
        var command = new Command("ingest", "Ingest a single item by identifier");

        var sourceOption = new Option<string>("--source") { Description = "Data source: jira", Arity = ArgumentArity.ExactlyOne };
        var idOption = new Option<string>("--id") { Description = "Item identifier (e.g., FHIR-43499)", Arity = ArgumentArity.ExactlyOne };
        var cookieOption = new Option<string?>("--jira-cookie") { Description = "Jira session cookie" };
        var apiTokenOption = new Option<string?>("--jira-api-token") { Description = "Jira API token" };
        var emailOption = new Option<string?>("--jira-email") { Description = "Jira email for API token auth" };

        command.Add(sourceOption);
        command.Add(idOption);
        command.Add(cookieOption);
        command.Add(apiTokenOption);
        command.Add(emailOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceOption)!;
            var id = parseResult.GetValue(idOption)!;
            var dbPath = parseResult.GetValue(dbOption)!;
            var verbose = parseResult.GetValue(verboseOption);

            if (source != "jira")
            {
                Console.Error.WriteLine($"Source '{source}' is not supported in Phase 1.");
                return;
            }

            var dbService = new DatabaseService(dbPath);
            dbService.InitializeDatabase();

            var jiraOptions = DownloadCommand.BuildJiraOptions(parseResult, cookieOption, apiTokenOption, emailOption);
            using var httpClient = JiraAuthHandler.CreateHttpClient(jiraOptions);
            var jiraSource = new JiraSource(jiraOptions, httpClient);

            var options = new IngestionOptions
            {
                DatabasePath = dbPath,
                Verbose = verbose,
            };

            Console.WriteLine($"Ingesting {source} item: {id}...");
            var result = await jiraSource.IngestItemAsync(id, options, ct);
            DownloadCommand.PrintResult(result, parseResult.GetValue(jsonOption));
        });

        return command;
    }
}
