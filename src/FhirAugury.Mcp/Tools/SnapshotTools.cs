using System.ComponentModel;
using System.Text;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Indexing;
using ModelContextProtocol.Server;

namespace FhirAugury.Mcp.Tools;

[McpServerToolType]
public static class SnapshotTools
{
    [McpServerTool, Description("Get a detailed markdown snapshot of a Jira issue including metadata, description, comments, and cross-references.")]
    public static string SnapshotJiraIssue(
        DatabaseService db,
        [Description("Issue key")] string key,
        [Description("Include comments (default true)")] bool includeComments = true,
        [Description("Include cross-references from other sources")] bool includeXrefs = true)
    {
        using var conn = db.OpenConnection();
        var issue = JiraIssueRecord.SelectSingle(conn, Key: key);
        if (issue is null)
            return $"Jira issue '{key}' not found.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {issue.Key}: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Type:** {issue.Type}  |  **Priority:** {issue.Priority}  |  **Status:** {issue.Status}");

        if (!string.IsNullOrEmpty(issue.Resolution))
            sb.AppendLine($"**Resolution:** {issue.Resolution}");

        sb.AppendLine($"**Assignee:** {issue.Assignee ?? "Unassigned"}  |  **Reporter:** {issue.Reporter ?? "Unknown"}");
        sb.AppendLine($"**Created:** {issue.CreatedAt:yyyy-MM-dd}  |  **Updated:** {issue.UpdatedAt:yyyy-MM-dd}");

        if (issue.ResolvedAt.HasValue)
            sb.AppendLine($"**Resolved:** {issue.ResolvedAt:yyyy-MM-dd}");

        sb.AppendLine();

        // Custom fields
        var customFields = new (string Label, string? Value)[]
        {
            ("Specification", issue.Specification),
            ("Work Group", issue.WorkGroup),
            ("Raised in Version", issue.RaisedInVersion),
            ("Selected Ballot", issue.SelectedBallot),
            ("Change Type", issue.ChangeType),
            ("Impact", issue.Impact),
            ("Vote", issue.Vote),
            ("Related Artifacts", issue.RelatedArtifacts),
            ("Related Issues", issue.RelatedIssues),
            ("Duplicate Of", issue.DuplicateOf),
            ("Applied Versions", issue.AppliedVersions),
            ("Labels", issue.Labels),
        };

        var hasCustom = false;
        foreach (var (label, value) in customFields)
        {
            if (!string.IsNullOrEmpty(value))
            {
                if (!hasCustom)
                {
                    sb.AppendLine("## Custom Fields");
                    hasCustom = true;
                }
                sb.AppendLine($"- **{label}:** {value}");
            }
        }
        if (hasCustom) sb.AppendLine();

        if (!string.IsNullOrEmpty(issue.Description))
        {
            sb.AppendLine("## Description");
            sb.AppendLine(issue.Description);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(issue.ResolutionDescription))
        {
            sb.AppendLine("## Resolution Description");
            sb.AppendLine(issue.ResolutionDescription);
            sb.AppendLine();
        }

        if (includeComments)
        {
            var comments = JiraCommentRecord.SelectList(conn, IssueKey: key,
                orderByProperties: ["CreatedAt"], orderByDirection: "ASC");
            if (comments.Count > 0)
            {
                sb.AppendLine($"## Comments ({comments.Count})");
                sb.AppendLine();
                foreach (var c in comments)
                {
                    sb.AppendLine($"### {c.Author} — {c.CreatedAt:yyyy-MM-dd HH:mm}");
                    sb.AppendLine(c.Body);
                    sb.AppendLine();
                }
            }
        }

        if (includeXrefs)
            AppendCrossReferences(sb, conn, "jira", key);

        sb.AppendLine("---");
        sb.AppendLine($"*URL: https://jira.hl7.org/browse/{issue.Key}*");
        return sb.ToString();
    }

    [McpServerTool, Description("Get a detailed markdown snapshot of a Zulip topic thread.")]
    public static string SnapshotZulipThread(
        DatabaseService db,
        [Description("Stream name")] string stream,
        [Description("Topic name")] string topic,
        [Description("Include cross-references")] bool includeXrefs = true)
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
        sb.AppendLine($"**First message:** {messages[0].Timestamp:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"**Last message:** {messages[^1].Timestamp:yyyy-MM-dd HH:mm}");

        var participants = messages.Select(m => m.SenderName).Distinct().ToList();
        sb.AppendLine($"**Participants:** {string.Join(", ", participants)}");
        sb.AppendLine();

        sb.AppendLine("## Messages");
        sb.AppendLine();
        foreach (var msg in messages)
        {
            sb.AppendLine($"### {msg.SenderName} — {msg.Timestamp:yyyy-MM-dd HH:mm}");
            sb.AppendLine(msg.ContentPlain);
            sb.AppendLine();
        }

        var zulipId = $"{stream}:{topic}";
        if (includeXrefs)
            AppendCrossReferences(sb, conn, "zulip", zulipId);

        sb.AppendLine("---");
        sb.AppendLine($"*URL: https://chat.fhir.org/#narrow/stream/{Uri.EscapeDataString(stream)}/topic/{Uri.EscapeDataString(topic)}*");
        return sb.ToString();
    }

    [McpServerTool, Description("Get a detailed markdown snapshot of a Confluence page.")]
    public static string SnapshotConfluencePage(
        DatabaseService db,
        [Description("Confluence page ID")] string pageId,
        [Description("Include cross-references")] bool includeXrefs = true)
    {
        using var conn = db.OpenConnection();
        var page = ConfluencePageRecord.SelectSingle(conn, ConfluenceId: pageId);
        if (page is null)
            return $"Confluence page '{pageId}' not found.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {page.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Space:** {page.SpaceKey}  |  **Version:** {page.VersionNumber}");
        sb.AppendLine($"**Last Modified By:** {page.LastModifiedBy ?? "Unknown"}  |  **Last Modified:** {page.LastModifiedAt:yyyy-MM-dd}");

        if (!string.IsNullOrEmpty(page.Labels))
            sb.AppendLine($"**Labels:** {page.Labels}");

        sb.AppendLine();

        if (!string.IsNullOrEmpty(page.BodyPlain))
        {
            sb.AppendLine("## Content");
            sb.AppendLine(page.BodyPlain);
            sb.AppendLine();
        }

        var comments = ConfluenceCommentRecord.SelectList(conn, ConfluencePageId: pageId,
            orderByProperties: ["CreatedAt"], orderByDirection: "ASC");
        if (comments.Count > 0)
        {
            sb.AppendLine($"## Comments ({comments.Count})");
            sb.AppendLine();
            foreach (var c in comments)
            {
                sb.AppendLine($"### {c.Author} — {c.CreatedAt:yyyy-MM-dd HH:mm}");
                sb.AppendLine(c.Body);
                sb.AppendLine();
            }
        }

        if (includeXrefs)
            AppendCrossReferences(sb, conn, "confluence", pageId);

        sb.AppendLine("---");
        sb.AppendLine($"*URL: {page.Url}*");
        return sb.ToString();
    }

    internal static void AppendCrossReferences(StringBuilder sb, Microsoft.Data.Sqlite.SqliteConnection conn, string sourceType, string sourceId)
    {
        var xrefs = CrossRefQueryService.GetRelatedItems(conn, sourceType, sourceId);
        if (xrefs.Count == 0) return;

        sb.AppendLine("## Cross-References");
        sb.AppendLine();

        foreach (var link in xrefs)
        {
            var direction = link.SourceType == sourceType && link.SourceId == sourceId ? "→" : "←";
            var otherType = direction == "→" ? link.TargetType : link.SourceType;
            var otherId = direction == "→" ? link.TargetId : link.SourceId;

            sb.AppendLine($"- {direction} [{otherType}] {otherId}");
            if (!string.IsNullOrEmpty(link.Context))
                sb.AppendLine($"  Context: {link.Context}");
        }
        sb.AppendLine();
    }
}
