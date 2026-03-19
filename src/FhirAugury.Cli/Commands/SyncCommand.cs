using System.CommandLine;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Models;
using FhirAugury.Models.Caching;
using FhirAugury.Sources.Confluence;
using FhirAugury.Sources.GitHub;
using FhirAugury.Sources.Jira;
using FhirAugury.Sources.Zulip;

namespace FhirAugury.Cli.Commands;

public static class SyncCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> verboseOption, Option<bool> jsonOption)
    {
        var command = new Command("sync", "Incremental update since last sync");

        var sourceOption = new Option<string>("--source") { Description = "Data source: jira, zulip, confluence, github, all", Arity = ArgumentArity.ExactlyOne };
        var sinceOption = new Option<DateTimeOffset?>("--since") { Description = "Override: sync from specific date" };
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
        var cachePathOption = new Option<string?>("--cache-path") { Description = "Override the cache root directory (default: ./cache)" };
        var cacheModeOption = new Option<CacheMode>("--cache-mode") { Description = "Cache mode: Disabled, WriteThrough, CacheOnly, WriteOnly", DefaultValueFactory = _ => CacheMode.WriteThrough };

        command.Add(sourceOption);
        command.Add(sinceOption);
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
            var sinceOverride = parseResult.GetValue(sinceOption);
            var json = parseResult.GetValue(jsonOption);
            var cacheMode = parseResult.GetValue(cacheModeOption);
            var cachePath = parseResult.GetValue(cachePathOption) ?? "./cache";

            IResponseCache cache = cacheMode == CacheMode.Disabled
                ? NullResponseCache.Instance
                : new FileSystemResponseCache(Path.GetFullPath(cachePath));

            var dbService = new DatabaseService(dbPath);
            dbService.InitializeDatabase();

            var sourcesToSync = source == "all" ? new[] { "jira", "zulip", "confluence", "github" } : new[] { source };

            foreach (var src in sourcesToSync)
            {
                switch (src)
                {
                    case "jira":
                    {
                        var since = ResolveSinceTime(dbService, sinceOverride, "jira");
                        var jiraOptions = DownloadCommand.BuildJiraOptions(parseResult, cookieOption, apiTokenOption, emailOption, cacheMode, cache);
                        using var httpClient = JiraAuthHandler.CreateHttpClient(jiraOptions);
                        var jiraSource = new JiraSource(jiraOptions, httpClient);

                        var options = new IngestionOptions { DatabasePath = dbPath, Verbose = verbose };
                        Console.WriteLine($"Syncing jira since {since:yyyy-MM-dd HH:mm} (cache: {cacheMode})...");
                        var result = await jiraSource.DownloadIncrementalAsync(since, options, ct);
                        UpdateSyncState(dbService, "jira", result);
                        DownloadCommand.PrintResult(result, json);
                        break;
                    }

                    case "zulip":
                    {
                        var since = ResolveSinceTime(dbService, sinceOverride, "zulip");
                        var zulipOptions = DownloadCommand.BuildZulipOptions(parseResult, zulipEmailOption, zulipApiKeyOption, zulipRcOption, cacheMode, cache);
                        using var httpClient = ZulipAuthHandler.CreateHttpClient(zulipOptions);
                        var zulipSource = new ZulipSource(zulipOptions, httpClient);

                        var options = new IngestionOptions { DatabasePath = dbPath, Verbose = verbose };
                        Console.WriteLine($"Syncing zulip since {since:yyyy-MM-dd HH:mm} (cache: {cacheMode})...");
                        var result = await zulipSource.DownloadIncrementalAsync(since, options, ct);
                        UpdateSyncState(dbService, "zulip", result);
                        DownloadCommand.PrintResult(result, json);
                        break;
                    }

                    default:
                        Console.Error.WriteLine($"Source '{src}' is not supported. Available: jira, zulip, confluence, github, all");
                        break;

                    case "confluence":
                    {
                        var since = ResolveSinceTime(dbService, sinceOverride, "confluence");
                        var confluenceOptions = DownloadCommand.BuildConfluenceOptions(parseResult, confluenceCookieOption, confluenceUserOption, confluenceTokenOption, cacheMode, cache);
                        using var httpClient = ConfluenceAuthHandler.CreateHttpClient(confluenceOptions);
                        var confluenceSource = new ConfluenceSource(confluenceOptions, httpClient);

                        var options = new IngestionOptions { DatabasePath = dbPath, Verbose = verbose };
                        Console.WriteLine($"Syncing confluence since {since:yyyy-MM-dd HH:mm} (cache: {cacheMode})...");
                        var result = await confluenceSource.DownloadIncrementalAsync(since, options, ct);
                        UpdateSyncState(dbService, "confluence", result);
                        DownloadCommand.PrintResult(result, json);
                        break;
                    }

                    case "github":
                    {
                        var since = ResolveSinceTime(dbService, sinceOverride, "github");
                        var githubOptions = DownloadCommand.BuildGitHubOptions(parseResult, githubPatOption);
                        using var httpClient = GitHubRateLimiter.CreateHttpClient(githubOptions);
                        var githubSource = new GitHubSource(githubOptions, httpClient);

                        var options = new IngestionOptions { DatabasePath = dbPath, Verbose = verbose };
                        Console.WriteLine($"Syncing github since {since:yyyy-MM-dd HH:mm}...");
                        var result = await githubSource.DownloadIncrementalAsync(since, options, ct);
                        UpdateSyncState(dbService, "github", result);
                        DownloadCommand.PrintResult(result, json);
                        break;
                    }
                }
            }
        });

        return command;
    }

    private static DateTimeOffset ResolveSinceTime(DatabaseService dbService, DateTimeOffset? sinceOverride, string source)
    {
        if (sinceOverride.HasValue) return sinceOverride.Value;

        using var conn = dbService.OpenConnection();
        var syncState = SyncStateRecord.SelectSingle(conn, SourceName: source);
        return syncState?.LastSyncAt ?? DateTimeOffset.MinValue;
    }

    private static void UpdateSyncState(DatabaseService dbService, string source, IngestionResult result)
    {
        using var conn = dbService.OpenConnection();
        var existing = SyncStateRecord.SelectSingle(conn, SourceName: source);
        if (existing is not null)
        {
            existing.LastSyncAt = result.CompletedAt;
            existing.ItemsIngested += result.ItemsProcessed;
            existing.Status = "completed";
            existing.LastError = result.Errors.Count > 0 ? result.Errors[0].Message : null;
            SyncStateRecord.Update(conn, existing);
        }
        else
        {
            SyncStateRecord.Insert(conn, new SyncStateRecord
            {
                Id = SyncStateRecord.GetIndex(),
                SourceName = source,
                SubSource = null,
                LastSyncAt = result.CompletedAt,
                LastCursor = null,
                ItemsIngested = result.ItemsProcessed,
                SyncSchedule = null,
                NextScheduledAt = null,
                Status = "completed",
                LastError = result.Errors.Count > 0 ? result.Errors[0].Message : null,
            });
        }
    }
}
