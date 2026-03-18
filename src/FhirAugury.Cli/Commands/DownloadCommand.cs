using System.CommandLine;
using FhirAugury.Database;
using FhirAugury.Models;
using FhirAugury.Sources.Jira;

namespace FhirAugury.Cli.Commands;

public static class DownloadCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> verboseOption, Option<bool> jsonOption)
    {
        var command = new Command("download", "Full download from a data source");

        var sourceOption = new Option<string>("--source") { Description = "Data source: jira", Arity = ArgumentArity.ExactlyOne };
        var filterOption = new Option<string?>("--filter") { Description = "Source-specific filter (e.g., JQL for Jira)" };
        var cookieOption = new Option<string?>("--jira-cookie") { Description = "Jira session cookie for authentication" };
        var apiTokenOption = new Option<string?>("--jira-api-token") { Description = "Jira API token" };
        var emailOption = new Option<string?>("--jira-email") { Description = "Jira email for API token auth" };

        command.Add(sourceOption);
        command.Add(filterOption);
        command.Add(cookieOption);
        command.Add(apiTokenOption);
        command.Add(emailOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceOption)!;
            var dbPath = parseResult.GetValue(dbOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            var filter = parseResult.GetValue(filterOption);

            if (source != "jira")
            {
                Console.Error.WriteLine($"Source '{source}' is not supported in Phase 1. Only 'jira' is available.");
                return;
            }

            var jiraOptions = BuildJiraOptions(parseResult, cookieOption, apiTokenOption, emailOption);
            var dbService = new DatabaseService(dbPath);
            dbService.InitializeDatabase();

            using var httpClient = JiraAuthHandler.CreateHttpClient(jiraOptions);
            var jiraSource = new JiraSource(jiraOptions, httpClient);

            var options = new IngestionOptions
            {
                DatabasePath = dbPath,
                Filter = filter,
                Verbose = verbose,
            };

            Console.WriteLine($"Starting full download from {source}...");
            var result = await jiraSource.DownloadAllAsync(options, ct);
            PrintResult(result, parseResult.GetValue(jsonOption));
        });

        return command;
    }

    internal static JiraSourceOptions BuildJiraOptions(
        System.CommandLine.ParseResult parseResult,
        Option<string?> cookieOption,
        Option<string?> apiTokenOption,
        Option<string?> emailOption)
    {
        var cookie = parseResult.GetValue(cookieOption);
        var apiToken = parseResult.GetValue(apiTokenOption);
        var email = parseResult.GetValue(emailOption);

        if (!string.IsNullOrEmpty(apiToken) && !string.IsNullOrEmpty(email))
        {
            return new JiraSourceOptions
            {
                AuthMode = JiraAuthMode.ApiToken,
                ApiToken = apiToken,
                Email = email,
            };
        }

        return new JiraSourceOptions
        {
            AuthMode = JiraAuthMode.Cookie,
            Cookie = cookie,
        };
    }

    internal static void PrintResult(IngestionResult result, bool json)
    {
        if (json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        var elapsed = result.CompletedAt - result.StartedAt;
        Console.WriteLine($"Download complete in {elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  Processed: {result.ItemsProcessed}");
        Console.WriteLine($"  New:       {result.ItemsNew}");
        Console.WriteLine($"  Updated:   {result.ItemsUpdated}");
        Console.WriteLine($"  Failed:    {result.ItemsFailed}");

        if (result.Errors.Count > 0)
        {
            Console.WriteLine($"  Errors:");
            foreach (var error in result.Errors.Take(10))
            {
                Console.WriteLine($"    [{error.ItemId}] {error.Message}");
            }
        }
    }
}
