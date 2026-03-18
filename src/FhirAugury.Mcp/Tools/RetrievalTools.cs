using System.ComponentModel;
using System.Text;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using ModelContextProtocol.Server;

namespace FhirAugury.Mcp.Tools;

[McpServerToolType]
public static class RetrievalTools
{
    [McpServerTool, Description("Get full details of a Jira issue by its key.")]
    public static string GetJiraIssue(
        DatabaseService db,
        [Description("Issue key, e.g. FHIR-43499")] string key)
    {
        using var conn = db.OpenConnection();
        var issue = JiraIssueRecord.SelectSingle(conn, Key: key);
        if (issue is null)
            return $"Jira issue '{key}' not found.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {issue.Key}: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine($"- **Type:** {issue.Type}");
        sb.AppendLine($"- **Priority:** {issue.Priority}");
        sb.AppendLine($"- **Status:** {issue.Status}");
        if (!string.IsNullOrEmpty(issue.Resolution))
            sb.AppendLine($"- **Resolution:** {issue.Resolution}");
        sb.AppendLine($"- **Assignee:** {issue.Assignee ?? "Unassigned"}");
        sb.AppendLine($"- **Reporter:** {issue.Reporter ?? "Unknown"}");
        sb.AppendLine($"- **Created:** {issue.CreatedAt:yyyy-MM-dd}");
        sb.AppendLine($"- **Updated:** {issue.UpdatedAt:yyyy-MM-dd}");
        if (issue.ResolvedAt.HasValue)
            sb.AppendLine($"- **Resolved:** {issue.ResolvedAt:yyyy-MM-dd}");

        AppendIfNotEmpty(sb, "Specification", issue.Specification);
        AppendIfNotEmpty(sb, "Work Group", issue.WorkGroup);
        AppendIfNotEmpty(sb, "Labels", issue.Labels);

        if (!string.IsNullOrEmpty(issue.Description))
        {
            sb.AppendLine();
            sb.AppendLine("## Description");
            sb.AppendLine(issue.Description);
        }

        if (!string.IsNullOrEmpty(issue.ResolutionDescription))
        {
            sb.AppendLine();
            sb.AppendLine("## Resolution Description");
            sb.AppendLine(issue.ResolutionDescription);
        }

        sb.AppendLine();
        sb.AppendLine($"**URL:** https://jira.hl7.org/browse/{issue.Key}");
        return sb.ToString();
    }

    [McpServerTool, Description("Get comments on a Jira issue.")]
    public static string GetJiraComments(
        DatabaseService db,
        [Description("Issue key")] string key,
        [Description("Maximum comments (default 50)")] int limit = 50)
    {
        using var conn = db.OpenConnection();
        var issue = JiraIssueRecord.SelectSingle(conn, Key: key);
        if (issue is null)
            return $"Jira issue '{key}' not found.";

        var comments = JiraCommentRecord.SelectList(conn, IssueKey: key,
            orderByProperties: ["CreatedAt"], orderByDirection: "ASC", resultLimit: limit);

        if (comments.Count == 0)
            return $"No comments on {key}.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Comments on {key} ({comments.Count})");
        sb.AppendLine();

        foreach (var c in comments)
        {
            sb.AppendLine($"### {c.Author} — {c.CreatedAt:yyyy-MM-dd HH:mm}");
            sb.AppendLine(c.Body);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool, Description("Get a full Zulip topic thread with all messages.")]
    public static string GetZulipThread(
        DatabaseService db,
        [Description("Stream name")] string stream,
        [Description("Topic name")] string topic)
    {
        using var conn = db.OpenConnection();
        var messages = ZulipMessageRecord.SelectList(conn, StreamName: stream, Topic: topic,
            orderByProperties: ["Timestamp"], orderByDirection: "ASC");

        if (messages.Count == 0)
            return $"No messages found in stream '{stream}', topic '{topic}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {stream} > {topic}");
        sb.AppendLine();
        sb.AppendLine($"**Messages:** {messages.Count}");
        sb.AppendLine($"**First:** {messages[0].Timestamp:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"**Last:** {messages[^1].Timestamp:yyyy-MM-dd HH:mm}");

        var participants = messages.Select(m => m.SenderName).Distinct().ToList();
        sb.AppendLine($"**Participants:** {string.Join(", ", participants)}");
        sb.AppendLine();

        foreach (var msg in messages)
        {
            sb.AppendLine($"### {msg.SenderName} — {msg.Timestamp:yyyy-MM-dd HH:mm}");
            sb.AppendLine(msg.ContentPlain);
            sb.AppendLine();
        }

        sb.AppendLine($"**URL:** https://chat.fhir.org/#narrow/stream/{Uri.EscapeDataString(stream)}/topic/{Uri.EscapeDataString(topic)}");
        return sb.ToString();
    }

    [McpServerTool, Description("Get full content of a Confluence page.")]
    public static string GetConfluencePage(
        DatabaseService db,
        [Description("Confluence page ID")] string? pageId = null,
        [Description("Page title (use with space)")] string? title = null,
        [Description("Space key (use with title)")] string? space = null)
    {
        using var conn = db.OpenConnection();
        ConfluencePageRecord? page = null;

        if (!string.IsNullOrEmpty(pageId))
        {
            page = ConfluencePageRecord.SelectSingle(conn, ConfluenceId: pageId);
        }
        else if (!string.IsNullOrEmpty(title))
        {
            page = ConfluencePageRecord.SelectSingle(conn, Title: title, SpaceKey: space);
        }

        if (page is null)
            return "Confluence page not found. Provide either pageId or title (with optional space).";

        var sb = new StringBuilder();
        sb.AppendLine($"# {page.Title}");
        sb.AppendLine();
        sb.AppendLine($"- **Space:** {page.SpaceKey}");
        sb.AppendLine($"- **Version:** {page.VersionNumber}");
        sb.AppendLine($"- **Last Modified By:** {page.LastModifiedBy ?? "Unknown"}");
        sb.AppendLine($"- **Last Modified:** {page.LastModifiedAt:yyyy-MM-dd}");
        if (!string.IsNullOrEmpty(page.Labels))
            sb.AppendLine($"- **Labels:** {page.Labels}");

        if (!string.IsNullOrEmpty(page.BodyPlain))
        {
            sb.AppendLine();
            sb.AppendLine("## Content");
            sb.AppendLine(page.BodyPlain);
        }

        sb.AppendLine();
        sb.AppendLine($"**URL:** {page.Url}");
        return sb.ToString();
    }

    [McpServerTool, Description("Get full details of a GitHub issue or pull request.")]
    public static string GetGithubIssue(
        DatabaseService db,
        [Description("Repository full name (e.g., HL7/fhir)")] string repo,
        [Description("Issue or PR number")] int number)
    {
        using var conn = db.OpenConnection();
        var uniqueKey = $"{repo}#{number}";
        var issue = GitHubIssueRecord.SelectSingle(conn, UniqueKey: uniqueKey);
        if (issue is null)
            return $"GitHub issue '{uniqueKey}' not found.";

        var typeLabel = issue.IsPullRequest ? "Pull Request" : "Issue";
        var sb = new StringBuilder();
        sb.AppendLine($"# {issue.RepoFullName}#{issue.Number}: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine($"- **Type:** {typeLabel}");
        sb.AppendLine($"- **State:** {issue.State}");
        sb.AppendLine($"- **Author:** {issue.Author ?? "Unknown"}");
        sb.AppendLine($"- **Created:** {issue.CreatedAt:yyyy-MM-dd}");
        sb.AppendLine($"- **Updated:** {issue.UpdatedAt:yyyy-MM-dd}");
        if (issue.ClosedAt.HasValue)
            sb.AppendLine($"- **Closed:** {issue.ClosedAt:yyyy-MM-dd}");
        if (!string.IsNullOrEmpty(issue.Labels))
            sb.AppendLine($"- **Labels:** {issue.Labels}");
        if (!string.IsNullOrEmpty(issue.Assignees))
            sb.AppendLine($"- **Assignees:** {issue.Assignees}");
        if (!string.IsNullOrEmpty(issue.Milestone))
            sb.AppendLine($"- **Milestone:** {issue.Milestone}");

        if (issue.IsPullRequest)
        {
            if (!string.IsNullOrEmpty(issue.HeadBranch))
                sb.AppendLine($"- **Branch:** {issue.HeadBranch} → {issue.BaseBranch}");
            if (!string.IsNullOrEmpty(issue.MergeState))
                sb.AppendLine($"- **Merge State:** {issue.MergeState}");
        }

        if (!string.IsNullOrEmpty(issue.Body))
        {
            sb.AppendLine();
            sb.AppendLine("## Description");
            sb.AppendLine(issue.Body);
        }

        var comments = GitHubCommentRecord.SelectList(conn,
            RepoFullName: issue.RepoFullName, IssueNumber: issue.Number,
            orderByProperties: ["CreatedAt"], orderByDirection: "ASC");

        if (comments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Comments ({comments.Count})");
            sb.AppendLine();
            foreach (var c in comments)
            {
                var reviewTag = c.IsReviewComment ? " (review)" : "";
                sb.AppendLine($"### {c.Author}{reviewTag} — {c.CreatedAt:yyyy-MM-dd HH:mm}");
                sb.AppendLine(c.Body);
                sb.AppendLine();
            }
        }

        var type = issue.IsPullRequest ? "pull" : "issues";
        sb.AppendLine($"**URL:** https://github.com/{issue.RepoFullName}/{type}/{issue.Number}");
        return sb.ToString();
    }

    private static void AppendIfNotEmpty(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            sb.AppendLine($"- **{label}:** {value}");
    }
}
