using System.Globalization;
using System.Text;
using FhirAugury.Common.Database;
using FhirAugury.Common.Database.Records;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Controllers;

/// <summary>Shared helper methods for GitHub API controllers.</summary>
internal static class GitHubUrlHelper
{
    internal static string BuildIssueUrl(string uniqueKey)
    {
        int hashIdx = uniqueKey.IndexOf('#');
        if (hashIdx < 0) return $"https://github.com/{uniqueKey}";
        string repo = uniqueKey[..hashIdx];
        string number = uniqueKey[(hashIdx + 1)..];
        return $"https://github.com/{repo}/issues/{number}";
    }

    /// <summary>Parses a file content ID in "owner/repo:path/to/file" format.</summary>
    internal static bool TryParseFileId(string id, out string? repoFullName, out string? filePath)
    {
        int colonIdx = id.IndexOf(':');
        if (colonIdx > 0 && id.Contains('/'))
        {
            repoFullName = id[..colonIdx];
            filePath = id[(colonIdx + 1)..];
            return !string.IsNullOrEmpty(filePath);
        }

        repoFullName = null;
        filePath = null;
        return false;
    }

    internal static GitHubFileContentRecord? LookupFileRecord(SqliteConnection connection, string repoFullName, string filePath)
    {
        List<GitHubFileContentRecord> results = GitHubFileContentRecord.SelectList(connection,
            RepoFullName: repoFullName, FilePath: filePath);
        return results.Count > 0 ? results[0] : null;
    }

    /// <summary>Parses an issue key in "owner/repo#number" format.</summary>
    internal static (string Repo, int Number) ParseIssueKey(string key)
    {
        int hashIdx = key.IndexOf('#');
        if (hashIdx < 0)
            throw new ArgumentException($"Invalid issue key format: {key}. Expected owner/repo#number.");
        string repo = key[..hashIdx];
        if (!int.TryParse(key[(hashIdx + 1)..], out int number))
            throw new ArgumentException($"Invalid issue number in key: {key}");
        return (repo, number);
    }

    internal record ResolvedItem(string Id, string Title, string Url, DateTimeOffset UpdatedAt);

    internal static ResolvedItem? ResolveXRef(SqliteConnection conn, ICrossReferenceRecord xref)
    {
        GitHubIssueRecord? issue = xref.ContentType switch
        {
            "issue" => GitHubIssueRecord.SelectSingle(conn, UniqueKey: xref.SourceId),
            "comment" => ResolveCommentToIssue(conn, xref.SourceId),
            "commit" => ResolveCommitToIssue(conn, xref.SourceId),
            _ => null,
        };

        if (issue is not null)
            return new(issue.UniqueKey, issue.Title, BuildIssueUrl(issue.UniqueKey), issue.UpdatedAt);

        if (xref.ContentType == "commit")
        {
            GitHubCommitRecord? commit = GitHubCommitRecord.SelectSingle(conn, Sha: xref.SourceId);
            if (commit is not null)
                return new(commit.Sha, commit.Message, commit.Url, commit.Date);
        }

        return null;
    }

    private static GitHubIssueRecord? ResolveCommentToIssue(SqliteConnection conn, string sourceId)
    {
        int hashIdx = sourceId.IndexOf('#');
        int colonIdx = sourceId.LastIndexOf(':');
        if (hashIdx < 0 || colonIdx < 0 || colonIdx <= hashIdx) return null;

        string issueKey = sourceId[..colonIdx];
        return GitHubIssueRecord.SelectSingle(conn, UniqueKey: issueKey);
    }

    private static GitHubIssueRecord? ResolveCommitToIssue(SqliteConnection conn, string sourceId)
    {
        GitHubCommitPrLinkRecord? prLink = GitHubCommitPrLinkRecord.SelectSingle(conn, CommitSha: sourceId);
        if (prLink is null) return null;

        string uniqueKey = $"{prLink.RepoFullName}#{prLink.PrNumber}";
        return GitHubIssueRecord.SelectSingle(conn, UniqueKey: uniqueKey);
    }

    internal static DateTimeOffset? ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        string str = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dt)
            ? dt
            : null;
    }

    internal static object MapCommitToJson(GitHubCommitRecord commit, SqliteConnection connection)
    {
        List<GitHubCommitFileRecord> files = GitHubCommitFileRecord.SelectList(connection, CommitSha: commit.Sha);
        return new
        {
            commit.Sha,
            commit.Message,
            commit.Author,
            commit.AuthorEmail,
            commit.Date,
            commit.Url,
            committerName = commit.CommitterName,
            committerEmail = commit.CommitterEmail,
            commit.FilesChanged,
            commit.Insertions,
            commit.Deletions,
            impact = commit.Insertions - commit.Deletions,
            commit.Body,
            commit.Refs,
            changedFiles = files.Select(f => f.FilePath).ToList(),
        };
    }

    internal static string BuildMarkdownSnapshot(
        SqliteConnection connection, GitHubIssueRecord issue, bool includeComments, bool includeRefs)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"# {issue.UniqueKey}: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine($"**State:** {issue.State}  ");
        sb.AppendLine($"**Type:** {(issue.IsPullRequest ? "Pull Request" : "Issue")}  ");
        if (issue.Author is not null) sb.AppendLine($"**Author:** {issue.Author}  ");
        if (issue.Assignees is not null) sb.AppendLine($"**Assignees:** {issue.Assignees}  ");
        if (issue.Labels is not null) sb.AppendLine($"**Labels:** {issue.Labels}  ");
        if (issue.Milestone is not null) sb.AppendLine($"**Milestone:** {issue.Milestone}  ");
        if (issue.MergeState is not null) sb.AppendLine($"**Merge State:** {issue.MergeState}  ");
        if (issue.HeadBranch is not null) sb.AppendLine($"**Head Branch:** {issue.HeadBranch}  ");
        if (issue.BaseBranch is not null) sb.AppendLine($"**Base Branch:** {issue.BaseBranch}  ");
        sb.AppendLine($"**Created:** {issue.CreatedAt:yyyy-MM-dd}  ");
        sb.AppendLine($"**Updated:** {issue.UpdatedAt:yyyy-MM-dd}  ");
        if (issue.ClosedAt is not null) sb.AppendLine($"**Closed:** {issue.ClosedAt:yyyy-MM-dd}  ");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(issue.Body))
        {
            sb.AppendLine("## Description");
            sb.AppendLine();
            sb.AppendLine(issue.Body);
            sb.AppendLine();
        }

        if (includeComments)
        {
            List<GitHubCommentRecord> comments = GitHubCommentRecord.SelectList(connection,
                RepoFullName: issue.RepoFullName, IssueNumber: issue.Number);
            if (comments.Count > 0)
            {
                sb.AppendLine("## Comments");
                sb.AppendLine();
                foreach (GitHubCommentRecord c in comments)
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
            List<JiraXRefRecord> jiraRefs = JiraXRefRecord.SelectList(connection, SourceId: issue.UniqueKey);
            if (jiraRefs.Count > 0)
            {
                sb.AppendLine("## Jira References");
                sb.AppendLine();
                foreach (JiraXRefRecord r in jiraRefs)
                    sb.AppendLine($"- {r.JiraKey}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}