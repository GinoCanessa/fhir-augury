using System.ComponentModel;
using System.Text;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using ModelContextProtocol.Server;

namespace FhirAugury.Mcp.Tools;

[McpServerToolType]
public static class AdminTools
{
    [McpServerTool, Description("Get database statistics: item counts, last sync times, database size.")]
    public static string GetStats(
        DatabaseService db,
        [Description("Optional: filter to specific source (jira, zulip, confluence, github)")] string? source = null)
    {
        using var conn = db.OpenConnection();
        var sb = new StringBuilder();

        if (source is null)
        {
            sb.AppendLine("## Database Statistics");
            sb.AppendLine();
            sb.AppendLine("| Category | Count |");
            sb.AppendLine("|----------|-------|");
            sb.AppendLine($"| Jira Issues | {JiraIssueRecord.SelectCount(conn)} |");
            sb.AppendLine($"| Jira Comments | {JiraCommentRecord.SelectCount(conn)} |");
            sb.AppendLine($"| Zulip Streams | {ZulipStreamRecord.SelectCount(conn)} |");
            sb.AppendLine($"| Zulip Messages | {ZulipMessageRecord.SelectCount(conn)} |");
            sb.AppendLine($"| Confluence Spaces | {ConfluenceSpaceRecord.SelectCount(conn)} |");
            sb.AppendLine($"| Confluence Pages | {ConfluencePageRecord.SelectCount(conn)} |");
            sb.AppendLine($"| GitHub Repos | {GitHubRepoRecord.SelectCount(conn)} |");
            sb.AppendLine($"| GitHub Issues/PRs | {GitHubIssueRecord.SelectCount(conn)} |");
            sb.AppendLine($"| Cross-Ref Links | {CrossRefLinkRecord.SelectCount(conn)} |");
            sb.AppendLine($"| BM25 Keywords | {KeywordRecord.SelectCount(conn)} |");
        }
        else
        {
            sb.AppendLine($"## {source} Statistics");
            sb.AppendLine();
            switch (source.ToLowerInvariant())
            {
                case "jira":
                    sb.AppendLine($"- **Issues:** {JiraIssueRecord.SelectCount(conn)}");
                    sb.AppendLine($"- **Comments:** {JiraCommentRecord.SelectCount(conn)}");
                    break;
                case "zulip":
                    sb.AppendLine($"- **Streams:** {ZulipStreamRecord.SelectCount(conn)}");
                    sb.AppendLine($"- **Messages:** {ZulipMessageRecord.SelectCount(conn)}");
                    break;
                case "confluence":
                    sb.AppendLine($"- **Spaces:** {ConfluenceSpaceRecord.SelectCount(conn)}");
                    sb.AppendLine($"- **Pages:** {ConfluencePageRecord.SelectCount(conn)}");
                    break;
                case "github":
                    sb.AppendLine($"- **Repos:** {GitHubRepoRecord.SelectCount(conn)}");
                    sb.AppendLine($"- **Issues/PRs:** {GitHubIssueRecord.SelectCount(conn)}");
                    sb.AppendLine($"- **Comments:** {GitHubCommentRecord.SelectCount(conn)}");
                    break;
                default:
                    return $"Unknown source '{source}'. Available: jira, zulip, confluence, github";
            }

            var syncState = SyncStateRecord.SelectSingle(conn, SourceName: source);
            if (syncState is not null)
            {
                sb.AppendLine();
                sb.AppendLine("### Sync State");
                sb.AppendLine($"- **Status:** {syncState.Status ?? "Unknown"}");
                sb.AppendLine($"- **Last Sync:** {syncState.LastSyncAt:yyyy-MM-dd HH:mm}");
                sb.AppendLine($"- **Items Ingested:** {syncState.ItemsIngested}");
                if (!string.IsNullOrEmpty(syncState.LastError))
                    sb.AppendLine($"- **Last Error:** {syncState.LastError}");
            }
        }

        return sb.ToString();
    }

    [McpServerTool, Description("Get sync status and schedule for all data sources.")]
    public static string GetSyncStatus(DatabaseService db)
    {
        using var conn = db.OpenConnection();
        var syncStates = SyncStateRecord.SelectList(conn);

        if (syncStates.Count == 0)
            return "No sync state recorded. The database may not have been synced yet.";

        var sb = new StringBuilder();
        sb.AppendLine("## Sync Status");
        sb.AppendLine();
        sb.AppendLine("| Source | Status | Last Sync | Items | Schedule | Next Run |");
        sb.AppendLine("|--------|--------|-----------|-------|----------|----------|");

        foreach (var s in syncStates)
        {
            var schedule = s.SyncSchedule ?? "—";
            var nextRun = s.NextScheduledAt.HasValue ? s.NextScheduledAt.Value.ToString("yyyy-MM-dd HH:mm") : "—";
            sb.AppendLine($"| {s.SourceName} | {s.Status ?? "—"} | {s.LastSyncAt:yyyy-MM-dd HH:mm} | {s.ItemsIngested} | {schedule} | {nextRun} |");
        }

        return sb.ToString();
    }
}
