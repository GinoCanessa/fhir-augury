using System.Text.Json;
using FhirAugury.Database.Records;

namespace FhirAugury.Sources.GitHub;

/// <summary>Maps GitHub API JSON responses to database records.</summary>
public static class GitHubIssueMapper
{
    /// <summary>Maps a GitHub issue/PR JSON element to a GitHubIssueRecord.</summary>
    public static GitHubIssueRecord MapIssue(JsonElement issueJson, string repoFullName)
    {
        var number = issueJson.GetProperty("number").GetInt32();
        var isPullRequest = issueJson.TryGetProperty("pull_request", out _);

        return new GitHubIssueRecord
        {
            Id = GitHubIssueRecord.GetIndex(),
            UniqueKey = $"{repoFullName}#{number}",
            RepoFullName = repoFullName,
            Number = number,
            IsPullRequest = isPullRequest,
            Title = issueJson.GetProperty("title").GetString() ?? string.Empty,
            Body = GetStringOrNull(issueJson, "body"),
            State = issueJson.GetProperty("state").GetString() ?? "open",
            Author = GetNestedString(issueJson, "user", "login"),
            Labels = ExtractLabels(issueJson),
            Assignees = ExtractAssignees(issueJson),
            Milestone = GetNestedString(issueJson, "milestone", "title"),
            CreatedAt = ParseDate(GetStringOrNull(issueJson, "created_at")),
            UpdatedAt = ParseDate(GetStringOrNull(issueJson, "updated_at")),
            ClosedAt = ParseNullableDate(GetStringOrNull(issueJson, "closed_at")),
            MergeState = isPullRequest ? GetPullRequestMergeState(issueJson) : null,
            HeadBranch = isPullRequest ? GetNestedString(issueJson, "head", "ref") : null,
            BaseBranch = isPullRequest ? GetNestedString(issueJson, "base", "ref") : null,
        };
    }

    /// <summary>Maps a GitHub comment JSON element to a GitHubCommentRecord.</summary>
    public static GitHubCommentRecord MapComment(JsonElement commentJson, int issueDbId, string repoFullName, int issueNumber, bool isReviewComment = false)
    {
        return new GitHubCommentRecord
        {
            Id = GitHubCommentRecord.GetIndex(),
            IssueId = issueDbId,
            RepoFullName = repoFullName,
            IssueNumber = issueNumber,
            Author = GetNestedString(commentJson, "user", "login") ?? "Unknown",
            CreatedAt = ParseDate(GetStringOrNull(commentJson, "created_at")),
            Body = commentJson.GetProperty("body").GetString() ?? string.Empty,
            IsReviewComment = isReviewComment,
        };
    }

    /// <summary>Maps a GitHub repo JSON element to a GitHubRepoRecord.</summary>
    public static GitHubRepoRecord MapRepo(JsonElement repoJson)
    {
        return new GitHubRepoRecord
        {
            Id = GitHubRepoRecord.GetIndex(),
            FullName = repoJson.GetProperty("full_name").GetString()!,
            Owner = GetNestedString(repoJson, "owner", "login") ?? string.Empty,
            Name = repoJson.GetProperty("name").GetString() ?? string.Empty,
            Description = GetStringOrNull(repoJson, "description"),
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
    }

    private static string? ExtractLabels(JsonElement issueJson)
    {
        if (!issueJson.TryGetProperty("labels", out var labelsArray) || labelsArray.ValueKind != JsonValueKind.Array)
            return null;

        var labels = new List<string>();
        foreach (var label in labelsArray.EnumerateArray())
        {
            var name = label.GetProperty("name").GetString();
            if (!string.IsNullOrEmpty(name)) labels.Add(name);
        }

        return labels.Count > 0 ? string.Join(",", labels) : null;
    }

    private static string? ExtractAssignees(JsonElement issueJson)
    {
        if (!issueJson.TryGetProperty("assignees", out var assigneesArray) || assigneesArray.ValueKind != JsonValueKind.Array)
            return null;

        var assignees = new List<string>();
        foreach (var assignee in assigneesArray.EnumerateArray())
        {
            var login = assignee.GetProperty("login").GetString();
            if (!string.IsNullOrEmpty(login)) assignees.Add(login);
        }

        return assignees.Count > 0 ? string.Join(",", assignees) : null;
    }

    private static string? GetPullRequestMergeState(JsonElement issueJson)
    {
        if (!issueJson.TryGetProperty("pull_request", out var pr))
            return null;

        if (pr.TryGetProperty("merged_at", out var mergedAt) && mergedAt.ValueKind != JsonValueKind.Null)
            return "merged";

        return null;
    }

    private static string? GetStringOrNull(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        return prop.GetString();
    }

    private static string? GetNestedString(JsonElement parent, string propertyName, string childPropertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        if (!prop.TryGetProperty(childPropertyName, out var child) || child.ValueKind == JsonValueKind.Null)
            return null;
        return child.GetString();
    }

    private static DateTimeOffset ParseDate(string? value) =>
        string.IsNullOrEmpty(value) ? DateTimeOffset.MinValue : DateTimeOffset.TryParse(value, out var dt) ? dt : DateTimeOffset.MinValue;

    private static DateTimeOffset? ParseNullableDate(string? value) =>
        string.IsNullOrEmpty(value) ? null : DateTimeOffset.TryParse(value, out var dt) ? dt : null;
}
