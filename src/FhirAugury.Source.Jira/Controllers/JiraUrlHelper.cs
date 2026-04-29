using System.Globalization;
using System.Text;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using FhirAugury.Common.Api;

namespace FhirAugury.Source.Jira.Controllers;

internal static class JiraUrlHelper
{
    internal static DateTimeOffset? ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        string str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt)
            ? dt
            : null;
    }

    internal static List<JiraIssueSummaryEntry> ReadIssueSummaries(SqliteCommand cmd, JiraServiceOptions options)
    {
        List<JiraIssueSummaryEntry> results = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = reader["Key"]?.ToString() ?? "";
            DateTimeOffset? updatedAt = null;
            if (reader["UpdatedAt"] is string updatedStr &&
                DateTimeOffset.TryParse(updatedStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset updated))
            {
                updatedAt = updated;
            }

            results.Add(new JiraIssueSummaryEntry
            {
                Key = key,
                ProjectKey = reader["ProjectKey"]?.ToString() ?? "",
                Title = reader["Title"]?.ToString() ?? "",
                Type = reader["Type"]?.ToString() ?? "",
                Status = reader["Status"]?.ToString() ?? "",
                Priority = reader["Priority"]?.ToString() ?? "",
                WorkGroup = reader["WorkGroup"]?.ToString() ?? "",
                Specification = reader["Specification"]?.ToString() ?? "",
                Url = $"{options.BaseUrl}/browse/{key}",
                UpdatedAt = updatedAt,
            });
        }
        return results;
    }

    internal static string BuildMarkdownSnapshot(
        SqliteConnection connection, JiraIssueRecord issue, bool includeComments, bool includeRefs)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"# {issue.Key}: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine($"**Status:** {issue.Status}  ");
        sb.AppendLine($"**Type:** {issue.Type}  ");
        sb.AppendLine($"**Priority:** {issue.Priority}  ");
        if (issue.Resolution is not null) sb.AppendLine($"**Resolution:** {issue.Resolution}  ");
        if (issue.Assignee is not null) sb.AppendLine($"**Assignee:** {issue.Assignee}  ");
        if (issue.Reporter is not null) sb.AppendLine($"**Reporter:** {issue.Reporter}  ");
        if (issue.WorkGroup is not null) sb.AppendLine($"**Work Group:** {issue.WorkGroup}  ");
        if (issue.Specification is not null) sb.AppendLine($"**Specification:** {issue.Specification}  ");
        if (issue.Labels is not null) sb.AppendLine($"**Labels:** {issue.Labels}  ");
        sb.AppendLine($"**Created:** {issue.CreatedAt:yyyy-MM-dd}  ");
        sb.AppendLine($"**Updated:** {issue.UpdatedAt:yyyy-MM-dd}  ");
        if (issue.ResolvedAt is not null) sb.AppendLine($"**Resolved:** {issue.ResolvedAt:yyyy-MM-dd}  ");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(issue.Description))
        {
            sb.AppendLine("## Description");
            sb.AppendLine();
            sb.AppendLine(issue.Description);
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(issue.ResolutionDescription))
        {
            sb.AppendLine("## Resolution Description");
            sb.AppendLine();
            sb.AppendLine(issue.ResolutionDescription);
            sb.AppendLine();
        }

        if (includeComments)
        {
            List<JiraCommentRecord> comments = JiraCommentRecord.SelectList(connection, IssueKey: issue.Key);
            if (comments.Count > 0)
            {
                sb.AppendLine("## Comments");
                sb.AppendLine();
                foreach (JiraCommentRecord c in comments)
                {
                    sb.AppendLine($"### {c.Author} ({c.CreatedAt:yyyy-MM-dd})");
                    sb.AppendLine();
                    sb.AppendLine(c.Body);
                    sb.AppendLine();
                }
            }
        }

        if (includeRefs)
        {
            List<JiraIssueLinkRecord> links = JiraIssueLinkRecord.SelectList(connection, SourceKey: issue.Key);
            List<JiraIssueLinkRecord> targetLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: issue.Key);

            if (links.Count > 0 || targetLinks.Count > 0)
            {
                sb.AppendLine("## Related Issues");
                sb.AppendLine();
                foreach (JiraIssueLinkRecord l in links)
                    sb.AppendLine($"- **{l.LinkType}** → {l.TargetKey}");
                foreach (JiraIssueLinkRecord l in targetLinks)
                    sb.AppendLine($"- **{l.LinkType}** ← {l.SourceKey}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}
