using System.CommandLine;
using FhirAugury.Database;
using FhirAugury.Models;
using FhirAugury.Sources.Confluence;
using FhirAugury.Sources.GitHub;
using FhirAugury.Sources.Jira;
using FhirAugury.Sources.Zulip;

namespace FhirAugury.Cli.Commands;

public static class IngestCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> verboseOption, Option<bool> jsonOption)
    {
        var command = new Command("ingest", "Ingest a single item by identifier");

        var sourceOption = new Option<string>("--source") { Description = "Data source: jira, zulip, confluence, github", Arity = ArgumentArity.ExactlyOne };
        var idOption = new Option<string>("--id") { Description = "Item identifier (e.g., FHIR-43499, stream:topic, pageId, owner/repo#number)", Arity = ArgumentArity.ExactlyOne };
        var cookieOption = new Option<string?>("--jira-cookie") { Description = "Jira session cookie" };
        var apiTokenOption = new Option<string?>("--jira-api-token") { Description = "Jira API token" };
        var emailOption = new Option<string?>("--jira-email") { Description = "Jira email for API token auth" };
        var zulipEmailOption = new Option<string?>("--zulip-email") { Description = "Zulip email" };
        var zulipApiKeyOption = new Option<string?>("--zulip-api-key") { Description = "Zulip API key" };
        var zulipRcOption = new Option<string?>("--zulip-rc") { Description = "Path to .zuliprc file" };
        var confluenceCookieOption = new Option<string?>("--confluence-cookie") { Description = "Confluence session cookie" };
        var confluenceUserOption = new Option<string?>("--confluence-user") { Description = "Confluence username" };
        var confluenceTokenOption = new Option<string?>("--confluence-token") { Description = "Confluence API token" };
        var githubPatOption = new Option<string?>("--github-pat") { Description = "GitHub personal access token" };

        command.Add(sourceOption);
        command.Add(idOption);
        command.Add(cookieOption);
        command.Add(apiTokenOption);
        command.Add(emailOption);
        command.Add(zulipEmailOption);
        command.Add(zulipApiKeyOption);
        command.Add(zulipRcOption);
        command.Add(confluenceCookieOption);
        command.Add(confluenceUserOption);
        command.Add(confluenceTokenOption);
        command.Add(githubPatOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceOption)!;
            var id = parseResult.GetValue(idOption)!;
            var dbPath = parseResult.GetValue(dbOption)!;
            var verbose = parseResult.GetValue(verboseOption);

            var dbService = new DatabaseService(dbPath);
            dbService.InitializeDatabase();

            var options = new IngestionOptions
            {
                DatabasePath = dbPath,
                Verbose = verbose,
            };

            IDataSource dataSource;
            HttpClient? httpClient = null;

            try
            {
                switch (source)
                {
                    case "jira":
                    {
                        var jiraOptions = DownloadCommand.BuildJiraOptions(parseResult, cookieOption, apiTokenOption, emailOption);
                        httpClient = JiraAuthHandler.CreateHttpClient(jiraOptions);
                        dataSource = new JiraSource(jiraOptions, httpClient);
                        break;
                    }

                    case "zulip":
                    {
                        var zulipOptions = DownloadCommand.BuildZulipOptions(parseResult, zulipEmailOption, zulipApiKeyOption, zulipRcOption);
                        httpClient = ZulipAuthHandler.CreateHttpClient(zulipOptions);
                        dataSource = new ZulipSource(zulipOptions, httpClient);
                        break;
                    }

                    case "confluence":
                    {
                        var confluenceOptions = DownloadCommand.BuildConfluenceOptions(parseResult, confluenceCookieOption, confluenceUserOption, confluenceTokenOption);
                        httpClient = ConfluenceAuthHandler.CreateHttpClient(confluenceOptions);
                        dataSource = new ConfluenceSource(confluenceOptions, httpClient);
                        break;
                    }

                    case "github":
                    {
                        var githubOptions = DownloadCommand.BuildGitHubOptions(parseResult, githubPatOption);
                        httpClient = GitHubRateLimiter.CreateHttpClient(githubOptions);
                        dataSource = new GitHubSource(githubOptions, httpClient);
                        break;
                    }

                    default:
                        Console.Error.WriteLine($"Source '{source}' is not supported. Available: jira, zulip, confluence, github");
                        return;
                }

                Console.WriteLine($"Ingesting {source} item: {id}...");
                var result = await dataSource.IngestItemAsync(id, options, ct);
                DownloadCommand.PrintResult(result, parseResult.GetValue(jsonOption));
            }
            finally
            {
                httpClient?.Dispose();
            }
        });

        return command;
    }
}
