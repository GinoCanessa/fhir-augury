using System.CommandLine;
using FhirAugury.Database;
using FhirAugury.Models;
using FhirAugury.Models.Caching;
using FhirAugury.Sources.Confluence;
using FhirAugury.Sources.GitHub;
using FhirAugury.Sources.Jira;
using FhirAugury.Sources.Zulip;

namespace FhirAugury.Cli.Commands;

public static class DownloadCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> verboseOption, Option<bool> jsonOption)
    {
        var command = new Command("download", "Full download from a data source");

        var sourceOption = new Option<string>("--source") { Description = "Data source: jira, zulip, confluence, github", Arity = ArgumentArity.ExactlyOne };
        var filterOption = new Option<string?>("--filter") { Description = "Source-specific filter (e.g., JQL for Jira)" };
        var cookieOption = new Option<string?>("--jira-cookie") { Description = "Jira session cookie for authentication" };
        var apiTokenOption = new Option<string?>("--jira-api-token") { Description = "Jira API token" };
        var emailOption = new Option<string?>("--jira-email") { Description = "Jira email for API token auth" };
        var zulipEmailOption = new Option<string?>("--zulip-email") { Description = "Zulip email for API authentication" };
        var zulipApiKeyOption = new Option<string?>("--zulip-api-key") { Description = "Zulip API key" };
        var zulipRcOption = new Option<string?>("--zulip-rc") { Description = "Path to .zuliprc file" };
        var confluenceCookieOption = new Option<string?>("--confluence-cookie") { Description = "Confluence session cookie" };
        var confluenceUserOption = new Option<string?>("--confluence-user") { Description = "Confluence username for Basic auth" };
        var confluenceTokenOption = new Option<string?>("--confluence-token") { Description = "Confluence API token" };
        var githubPatOption = new Option<string?>("--github-pat") { Description = "GitHub personal access token" };
        var cachePathOption = new Option<string?>("--cache-path") { Description = "Override the cache root directory (default: ./cache)" };
        var cacheModeOption = new Option<CacheMode>("--cache-mode") { Description = "Cache mode: Disabled, WriteThrough, CacheOnly, WriteOnly", DefaultValueFactory = _ => CacheMode.WriteThrough };

        command.Add(sourceOption);
        command.Add(filterOption);
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
        command.Add(cachePathOption);
        command.Add(cacheModeOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceOption)!;
            var dbPath = parseResult.GetValue(dbOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            var filter = parseResult.GetValue(filterOption);
            var cacheMode = parseResult.GetValue(cacheModeOption);
            var cachePath = parseResult.GetValue(cachePathOption) ?? "./cache";

            IResponseCache cache = cacheMode == CacheMode.Disabled
                ? NullResponseCache.Instance
                : new FileSystemResponseCache(Path.GetFullPath(cachePath));

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
                    var jiraOptions = BuildJiraOptions(parseResult, cookieOption, apiTokenOption, emailOption, cacheMode, cache);
                    using var httpClient = JiraAuthHandler.CreateHttpClient(jiraOptions);
                    dataSource = new JiraSource(jiraOptions, httpClient);
                    break;
                }

                case "zulip":
                {
                    var zulipOptions = BuildZulipOptions(parseResult, zulipEmailOption, zulipApiKeyOption, zulipRcOption, cacheMode, cache);
                    using var httpClient = ZulipAuthHandler.CreateHttpClient(zulipOptions);
                    dataSource = new ZulipSource(zulipOptions, httpClient);
                    break;
                }

                case "confluence":
                {
                    var confluenceOptions = BuildConfluenceOptions(parseResult, confluenceCookieOption, confluenceUserOption, confluenceTokenOption, cacheMode, cache);
                    using var httpClient = ConfluenceAuthHandler.CreateHttpClient(confluenceOptions);
                    dataSource = new ConfluenceSource(confluenceOptions, httpClient);
                    break;
                }

                case "github":
                {
                    var githubOptions = BuildGitHubOptions(parseResult, githubPatOption);
                    using var httpClient = GitHubRateLimiter.CreateHttpClient(githubOptions);
                    dataSource = new GitHubSource(githubOptions, httpClient);
                    break;
                }

                default:
                    Console.Error.WriteLine($"Source '{source}' is not supported. Available: jira, zulip, confluence, github");
                    return;
            }

            Console.WriteLine($"Starting full download from {source} (cache: {cacheMode})...");
            var result = await dataSource.DownloadAllAsync(ingestionOptions, ct);
            PrintResult(result, parseResult.GetValue(jsonOption));
        });

        return command;
    }

    internal static JiraSourceOptions BuildJiraOptions(
        System.CommandLine.ParseResult parseResult,
        Option<string?> cookieOption,
        Option<string?> apiTokenOption,
        Option<string?> emailOption,
        CacheMode cacheMode = CacheMode.Disabled,
        IResponseCache? cache = null)
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
                CacheMode = cacheMode,
                Cache = cache,
            };
        }

        return new JiraSourceOptions
        {
            AuthMode = JiraAuthMode.Cookie,
            Cookie = cookie,
            CacheMode = cacheMode,
            Cache = cache,
        };
    }

    internal static ZulipSourceOptions BuildZulipOptions(
        System.CommandLine.ParseResult parseResult,
        Option<string?> zulipEmailOption,
        Option<string?> zulipApiKeyOption,
        Option<string?> zulipRcOption,
        CacheMode cacheMode = CacheMode.Disabled,
        IResponseCache? cache = null)
    {
        return new ZulipSourceOptions
        {
            Email = parseResult.GetValue(zulipEmailOption),
            ApiKey = parseResult.GetValue(zulipApiKeyOption),
            CredentialFile = parseResult.GetValue(zulipRcOption),
            CacheMode = cacheMode,
            Cache = cache,
        };
    }

    internal static ConfluenceSourceOptions BuildConfluenceOptions(
        System.CommandLine.ParseResult parseResult,
        Option<string?> cookieOption,
        Option<string?> userOption,
        Option<string?> tokenOption,
        CacheMode cacheMode = CacheMode.Disabled,
        IResponseCache? cache = null)
    {
        var username = parseResult.GetValue(userOption);
        var token = parseResult.GetValue(tokenOption);
        var cookie = parseResult.GetValue(cookieOption);

        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(token))
        {
            return new ConfluenceSourceOptions
            {
                AuthMode = ConfluenceAuthMode.Basic,
                Username = username,
                ApiToken = token,
                CacheMode = cacheMode,
                Cache = cache,
            };
        }

        return new ConfluenceSourceOptions
        {
            AuthMode = ConfluenceAuthMode.Cookie,
            Cookie = cookie,
            CacheMode = cacheMode,
            Cache = cache,
        };
    }

    internal static GitHubSourceOptions BuildGitHubOptions(
        System.CommandLine.ParseResult parseResult,
        Option<string?> patOption)
    {
        return new GitHubSourceOptions
        {
            PersonalAccessToken = parseResult.GetValue(patOption),
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
