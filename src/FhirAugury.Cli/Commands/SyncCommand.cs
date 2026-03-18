using System.CommandLine;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Models;
using FhirAugury.Sources.Jira;

namespace FhirAugury.Cli.Commands;

public static class SyncCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> verboseOption, Option<bool> jsonOption)
    {
        var command = new Command("sync", "Incremental update since last sync");

        var sourceOption = new Option<string>("--source") { Description = "Data source: jira", Arity = ArgumentArity.ExactlyOne };
        var sinceOption = new Option<DateTimeOffset?>("--since") { Description = "Override: sync from specific date" };
        var cookieOption = new Option<string?>("--jira-cookie") { Description = "Jira session cookie" };
        var apiTokenOption = new Option<string?>("--jira-api-token") { Description = "Jira API token" };
        var emailOption = new Option<string?>("--jira-email") { Description = "Jira email for API token auth" };

        command.Add(sourceOption);
        command.Add(sinceOption);
        command.Add(cookieOption);
        command.Add(apiTokenOption);
        command.Add(emailOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceOption)!;
            var dbPath = parseResult.GetValue(dbOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            var sinceOverride = parseResult.GetValue(sinceOption);

            if (source != "jira")
            {
                Console.Error.WriteLine($"Source '{source}' is not supported in Phase 1.");
                return;
            }

            var dbService = new DatabaseService(dbPath);
            dbService.InitializeDatabase();

            // Determine sync-since time
            DateTimeOffset since;
            if (sinceOverride.HasValue)
            {
                since = sinceOverride.Value;
            }
            else
            {
                using var conn = dbService.OpenConnection();
                var syncState = SyncStateRecord.SelectSingle(conn, SourceName: source);
                since = syncState?.LastSyncAt ?? DateTimeOffset.MinValue;
            }

            var jiraOptions = DownloadCommand.BuildJiraOptions(parseResult, cookieOption, apiTokenOption, emailOption);
            using var httpClient = JiraAuthHandler.CreateHttpClient(jiraOptions);
            var jiraSource = new JiraSource(jiraOptions, httpClient);

            var options = new IngestionOptions
            {
                DatabasePath = dbPath,
                Verbose = verbose,
            };

            Console.WriteLine($"Syncing {source} since {since:yyyy-MM-dd HH:mm}...");
            var result = await jiraSource.DownloadIncrementalAsync(since, options, ct);

            // Update sync state
            using var updateConn = dbService.OpenConnection();
            var existingState = SyncStateRecord.SelectSingle(updateConn, SourceName: source);
            if (existingState is not null)
            {
                existingState.LastSyncAt = result.CompletedAt;
                existingState.ItemsIngested += result.ItemsProcessed;
                existingState.Status = "completed";
                existingState.LastError = result.Errors.Count > 0 ? result.Errors[0].Message : null;
                SyncStateRecord.Update(updateConn, existingState);
            }
            else
            {
                SyncStateRecord.Insert(updateConn, new SyncStateRecord
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

            DownloadCommand.PrintResult(result, parseResult.GetValue(jsonOption));
        });

        return command;
    }
}
