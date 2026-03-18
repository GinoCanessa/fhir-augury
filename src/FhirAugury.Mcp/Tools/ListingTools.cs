using System.ComponentModel;
using System.Text;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using ModelContextProtocol.Server;

namespace FhirAugury.Mcp.Tools;

[McpServerToolType]
public static class ListingTools
{
    [McpServerTool, Description("List Jira issues with filters and pagination.")]
    public static string ListJiraIssues(
        DatabaseService db,
        [Description("Filter by work group")] string? workGroup = null,
        [Description("Filter by status")] string? status = null,
        [Description("Filter by resolution")] string? resolution = null,
        [Description("Filter by specification")] string? specification = null,
        [Description("Sort by: updated, created, key (default updated)")] string sort = "updated",
        [Description("Maximum results")] int limit = 50,
        [Description("Offset for pagination")] int offset = 0)
    {
        using var conn = db.OpenConnection();

        var orderBy = sort.ToLowerInvariant() switch
        {
            "created" => "CreatedAt",
            "key" => "Key",
            _ => "UpdatedAt",
        };

        var issues = JiraIssueRecord.SelectList(conn,
            WorkGroup: workGroup, Status: status, Resolution: resolution,
            Specification: specification,
            orderByProperties: [orderBy], orderByDirection: "DESC",
            resultLimit: limit, resultOffset: offset);

        if (issues.Count == 0)
            return "No Jira issues found matching the filters.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Jira Issues ({issues.Count} results, offset {offset})");
        sb.AppendLine();
        sb.AppendLine("| Key | Title | Status | Work Group | Updated |");
        sb.AppendLine("|-----|-------|--------|------------|---------|");
        foreach (var i in issues)
        {
            var title = Truncate(i.Title, 50);
            sb.AppendLine($"| {i.Key} | {title} | {i.Status} | {i.WorkGroup ?? ""} | {i.UpdatedAt:yyyy-MM-dd} |");
        }
        return sb.ToString();
    }

    [McpServerTool, Description("List available Zulip streams.")]
    public static string ListZulipStreams(DatabaseService db)
    {
        using var conn = db.OpenConnection();
        var streams = ZulipStreamRecord.SelectList(conn,
            orderByProperties: ["Name"], orderByDirection: "ASC");

        if (streams.Count == 0)
            return "No Zulip streams indexed.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Zulip Streams ({streams.Count})");
        sb.AppendLine();
        sb.AppendLine("| Name | Messages | Description |");
        sb.AppendLine("|------|----------|-------------|");
        foreach (var s in streams)
        {
            var desc = Truncate(s.Description ?? "", 60);
            sb.AppendLine($"| {s.Name} | {s.MessageCount} | {desc} |");
        }
        return sb.ToString();
    }

    [McpServerTool, Description("List topics in a Zulip stream.")]
    public static string ListZulipTopics(
        DatabaseService db,
        [Description("Stream name")] string stream)
    {
        using var conn = db.OpenConnection();

        // Get distinct topics with message counts via raw SQL
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Topic, COUNT(*) as MsgCount, MAX(Timestamp) as LastMessage
            FROM zulip_messages
            WHERE StreamName = @stream
            GROUP BY Topic
            ORDER BY LastMessage DESC
            """;
        cmd.Parameters.AddWithValue("@stream", stream);

        using var reader = cmd.ExecuteReader();
        var topics = new List<(string Topic, int Count, string LastMessage)>();
        while (reader.Read())
        {
            topics.Add((
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2)));
        }

        if (topics.Count == 0)
            return $"No topics found in stream '{stream}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Topics in {stream} ({topics.Count})");
        sb.AppendLine();
        sb.AppendLine("| Topic | Messages | Last Activity |");
        sb.AppendLine("|-------|----------|---------------|");
        foreach (var (topic, count, lastMsg) in topics)
        {
            var topicName = Truncate(topic, 60);
            sb.AppendLine($"| {topicName} | {count} | {lastMsg} |");
        }
        return sb.ToString();
    }

    [McpServerTool, Description("List indexed Confluence spaces.")]
    public static string ListConfluenceSpaces(DatabaseService db)
    {
        using var conn = db.OpenConnection();
        var spaces = ConfluenceSpaceRecord.SelectList(conn,
            orderByProperties: ["Name"], orderByDirection: "ASC");

        if (spaces.Count == 0)
            return "No Confluence spaces indexed.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Confluence Spaces ({spaces.Count})");
        sb.AppendLine();
        sb.AppendLine("| Key | Name | URL |");
        sb.AppendLine("|-----|------|-----|");
        foreach (var s in spaces)
        {
            sb.AppendLine($"| {s.Key} | {s.Name} | {s.Url ?? ""} |");
        }
        return sb.ToString();
    }

    [McpServerTool, Description("List tracked GitHub repositories.")]
    public static string ListGithubRepos(DatabaseService db)
    {
        using var conn = db.OpenConnection();
        var repos = GitHubRepoRecord.SelectList(conn,
            orderByProperties: ["FullName"], orderByDirection: "ASC");

        if (repos.Count == 0)
            return "No GitHub repositories tracked.";

        var sb = new StringBuilder();
        sb.AppendLine($"## GitHub Repositories ({repos.Count})");
        sb.AppendLine();
        sb.AppendLine("| Repository | Description | Last Fetched |");
        sb.AppendLine("|------------|-------------|--------------|");
        foreach (var r in repos)
        {
            var desc = Truncate(r.Description ?? "", 60);
            sb.AppendLine($"| {r.FullName} | {desc} | {r.LastFetchedAt:yyyy-MM-dd} |");
        }
        return sb.ToString();
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var clean = text.ReplaceLineEndings(" ");
        return clean.Length <= maxLength ? clean : clean[..(maxLength - 3)] + "...";
    }
}
