using System.CommandLine;
using FhirAugury.Database;
using FhirAugury.Models;
using FhirAugury.Sources.Jira;
using FhirAugury.Sources.Zulip;

namespace FhirAugury.Cli.Commands;

public static class DownloadCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> verboseOption, Option<bool> jsonOption)
    {
        var command = new Command("download", "Full download from a data source");

        var sourceOption = new Option<string>("--source") { Description = "Data source: jira, zulip", Arity = ArgumentArity.ExactlyOne };
        var filterOption = new Option<string?>("--filter") { Description = "Source-specific filter (e.g., JQL for Jira)" };
        var cookieOption = new Option<string?>("--jira-cookie") { Description = "Jira session cookie for authentication" };
        var apiTokenOption = new Option<string?>("--jira-api-token") { Description = "Jira API token" };
        var emailOption = new Option<string?>("--jira-email") { Description = "Jira email for API token auth" };
        var zulipEmailOption = new Option<string?>("--zulip-email") { Description = "Zulip email for API authentication" };
        var zulipApiKeyOption = new Option<string?>("--zulip-api-key") { Description = "Zulip API key" };
        var zulipRcOption = new Option<string?>("--zulip-rc") { Description = "Path to .zuliprc file" };

        command.Add(sourceOption);
        command.Add(filterOption);
        command.Add(cookieOption);
        command.Add(apiTokenOption);
        command.Add(emailOption);
        command.Add(zulipEmailOption);
        command.Add(zulipApiKeyOption);
        command.Add(zulipRcOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceOption)!;
            var dbPath = parseResult.GetValue(dbOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            var filter = parseResult.GetValue(filterOption);

            var ingestionOptions = new IngestionOptions
            {
                DatabasePath = dbPath,
                Filter = filter,
                Verbose = verbose,
            };

            var dbService = new DatabaseService(dbPath);
            dbService.InitializeDatabase();

            IDataSource dataSource;

            switch (source)
            {
                case "jira":
                {
                    var jiraOptions = BuildJiraOptions(parseResult, cookieOption, apiTokenOption, emailOption);
                    using var httpClient = JiraAuthHandler.CreateHttpClient(jiraOptions);
                    dataSource = new JiraSource(jiraOptions, httpClient);
                    break;
                }

                case "zulip":
                {
                    var zulipOptions = BuildZulipOptions(parseResult, zulipEmailOption, zulipApiKeyOption, zulipRcOption);
                    using var httpClient = ZulipAuthHandler.CreateHttpClient(zulipOptions);
                    dataSource = new ZulipSource(zulipOptions, httpClient);
                    break;
                }

                default:
                    Console.Error.WriteLine($"Source '{source}' is not supported. Available: jira, zulip");
                    return;
            }

            Console.WriteLine($"Starting full download from {source}...");
            var result = await dataSource.DownloadAllAsync(ingestionOptions, ct);
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

    internal static ZulipSourceOptions BuildZulipOptions(
        System.CommandLine.ParseResult parseResult,
        Option<string?> zulipEmailOption,
        Option<string?> zulipApiKeyOption,
        Option<string?> zulipRcOption)
    {
        return new ZulipSourceOptions
        {
            Email = parseResult.GetValue(zulipEmailOption),
            ApiKey = parseResult.GetValue(zulipApiKeyOption),
            CredentialFile = parseResult.GetValue(zulipRcOption),
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
