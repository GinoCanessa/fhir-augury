using System.CommandLine;
using FhirAugury.Database;
using FhirAugury.Database.Records;

namespace FhirAugury.Cli.Commands;

public static class StatsCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> jsonOption)
    {
        var command = new Command("stats", "Show database statistics");

        var sourceOption = new Option<string?>("--source") { Description = "Filter to a specific source" };
        command.Add(sourceOption);

        command.SetAction((parseResult, _) =>
        {
            var dbPath = parseResult.GetValue(dbOption)!;
            var json = parseResult.GetValue(jsonOption);
            var source = parseResult.GetValue(sourceOption);

            if (!System.IO.File.Exists(dbPath))
            {
                Console.Error.WriteLine($"Database not found: {dbPath}");
                return Task.CompletedTask;
            }

            var dbService = new DatabaseService(dbPath, readOnly: true);
            using var conn = dbService.OpenConnection();

            var stats = new Dictionary<string, object>();

            if (source is null or "jira")
            {
                var issueCount = JiraIssueRecord.SelectCount(conn);
                var commentCount = JiraCommentRecord.SelectCount(conn);
                var syncState = SyncStateRecord.SelectSingle(conn, SourceName: "jira");

                stats["jira_issues"] = issueCount;
                stats["jira_comments"] = commentCount;
                stats["jira_last_sync"] = syncState?.LastSyncAt.ToString("yyyy-MM-dd HH:mm") ?? "never";
                stats["jira_sync_status"] = syncState?.Status ?? "none";
            }

            if (source is null or "zulip")
            {
                var streamCount = ZulipStreamRecord.SelectCount(conn);
                var messageCount = ZulipMessageRecord.SelectCount(conn);
                var syncState = SyncStateRecord.SelectSingle(conn, SourceName: "zulip");

                stats["zulip_streams"] = streamCount;
                stats["zulip_messages"] = messageCount;
                stats["zulip_last_sync"] = syncState?.LastSyncAt.ToString("yyyy-MM-dd HH:mm") ?? "never";
                stats["zulip_sync_status"] = syncState?.Status ?? "none";
            }

            var fileInfo = new System.IO.FileInfo(dbPath);
            stats["database_size_mb"] = Math.Round(fileInfo.Length / 1024.0 / 1024.0, 2);

            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(stats, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine("FHIR Augury Database Statistics");
                Console.WriteLine(new string('─', 40));
                foreach (var (key, value) in stats)
                {
                    Console.WriteLine($"  {key,-25} {value}");
                }
            }

            return Task.CompletedTask;
        });

        return command;
    }
}
