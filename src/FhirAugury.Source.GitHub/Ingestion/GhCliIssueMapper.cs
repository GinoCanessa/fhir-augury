using System.Text.Json;
using FhirAugury.Source.GitHub.Database.Records;
using static FhirAugury.Common.DateTimeHelper;
using static FhirAugury.Common.JsonElementHelper;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Maps gh CLI JSON output to database records.
/// Handles field name differences between the REST API and gh CLI JSON shapes.
/// </summary>
public static class GhCliIssueMapper
{
    /// <summary>
    /// Maps a <c>gh issue list --json</c> element to a <see cref="GitHubIssueRecord"/>.
    /// </summary>
    public static GitHubIssueRecord MapIssue(JsonElement json, string repoFullName)
    {
        int number = json.GetProperty("number").GetInt32();

        return new GitHubIssueRecord
        {
            Id = GitHubIssueRecord.GetIndex(),
            UniqueKey = $"{repoFullName}#{number}",
            RepoFullName = repoFullName,
            Number = number,
            IsPullRequest = false,
            Title = json.GetProperty("title").GetString() ?? string.Empty,
            Body = GetString(json, "body"),
            State = NormalizeState(json.GetProperty("state").GetString()),
            Author = GetNestedString(json, "author", "login"),
            Labels = ExtractLabels(json),
            Assignees = ExtractAssignees(json),
            Milestone = GetNestedString(json, "milestone", "title"),
            CreatedAt = ParseDate(GetString(json, "createdAt")),
            UpdatedAt = ParseDate(GetString(json, "updatedAt")),
            ClosedAt = ParseNullableDate(GetString(json, "closedAt")),
            MergeState = null,
            HeadBranch = null,
            BaseBranch = null,
        };
    }

    /// <summary>
    /// Maps a <c>gh pr list --json</c> element to a <see cref="GitHubIssueRecord"/>.
    /// </summary>
    public static GitHubIssueRecord MapPullRequest(JsonElement json, string repoFullName)
    {
        int number = json.GetProperty("number").GetInt32();

        string? mergedAt = GetString(json, "mergedAt");
        string? mergeState = !string.IsNullOrEmpty(mergedAt) ? "merged" : null;

        return new GitHubIssueRecord
        {
            Id = GitHubIssueRecord.GetIndex(),
            UniqueKey = $"{repoFullName}#{number}",
            RepoFullName = repoFullName,
            Number = number,
            IsPullRequest = true,
            Title = json.GetProperty("title").GetString() ?? string.Empty,
            Body = GetString(json, "body"),
            State = NormalizeState(json.GetProperty("state").GetString()),
            Author = GetNestedString(json, "author", "login"),
            Labels = ExtractLabels(json),
            Assignees = ExtractAssignees(json),
            Milestone = GetNestedString(json, "milestone", "title"),
            CreatedAt = ParseDate(GetString(json, "createdAt")),
            UpdatedAt = ParseDate(GetString(json, "updatedAt")),
            ClosedAt = ParseNullableDate(GetString(json, "closedAt")),
            MergeState = mergeState,
            HeadBranch = GetString(json, "headRefName"),
            BaseBranch = GetString(json, "baseRefName"),
        };
    }

    /// <summary>
    /// Maps a <c>gh repo view --json</c> element to a <see cref="GitHubRepoRecord"/>.
    /// </summary>
    public static GitHubRepoRecord MapRepo(JsonElement json)
    {
        return new GitHubRepoRecord
        {
            Id = GitHubRepoRecord.GetIndex(),
            FullName = GetString(json, "nameWithOwner") ?? string.Empty,
            Owner = GetNestedString(json, "owner", "login") ?? string.Empty,
            Name = json.GetProperty("name").GetString() ?? string.Empty,
            Description = GetString(json, "description"),
            HasIssues = json.TryGetProperty("hasIssuesEnabled", out JsonElement hi) && hi.GetBoolean(),
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Maps a comment element from <c>gh issue view --json comments</c> to a <see cref="GitHubCommentRecord"/>.
    /// </summary>
    public static GitHubCommentRecord MapComment(
        JsonElement json, int issueDbId, string repoFullName, int issueNumber, bool isReviewComment = false)
    {
        return new GitHubCommentRecord
        {
            Id = GitHubCommentRecord.GetIndex(),
            IssueId = issueDbId,
            RepoFullName = repoFullName,
            IssueNumber = issueNumber,
            Author = GetNestedString(json, "author", "login") ?? "Unknown",
            CreatedAt = ParseDate(GetString(json, "createdAt")),
            Body = json.GetProperty("body").GetString() ?? string.Empty,
            IsReviewComment = isReviewComment,
        };
    }

    /// <summary>
    /// Maps a review element from <c>gh pr view --json reviews</c> to a <see cref="GitHubCommentRecord"/>.
    /// </summary>
    public static GitHubCommentRecord MapReview(
        JsonElement json, int issueDbId, string repoFullName, int issueNumber)
    {
        return new GitHubCommentRecord
        {
            Id = GitHubCommentRecord.GetIndex(),
            IssueId = issueDbId,
            RepoFullName = repoFullName,
            IssueNumber = issueNumber,
            Author = GetNestedString(json, "author", "login") ?? "Unknown",
            CreatedAt = ParseDate(GetString(json, "submittedAt")),
            Body = json.GetProperty("body").GetString() ?? string.Empty,
            IsReviewComment = true,
        };
    }

    /// <summary>
    /// Normalizes gh CLI state values (OPEN, CLOSED, MERGED) to lowercase REST API format.
    /// </summary>
    private static string NormalizeState(string? state) =>
        state?.ToLowerInvariant() ?? "open";

    private static string? ExtractLabels(JsonElement json)
    {
        if (!json.TryGetProperty("labels", out JsonElement labelsArray) || labelsArray.ValueKind != JsonValueKind.Array)
            return null;

        List<string> labels = [];
        foreach (JsonElement label in labelsArray.EnumerateArray())
        {
            string? name = label.GetProperty("name").GetString();
            if (!string.IsNullOrEmpty(name)) labels.Add(name);
        }

        return labels.Count > 0 ? string.Join(",", labels) : null;
    }

    private static string? ExtractAssignees(JsonElement json)
    {
        if (!json.TryGetProperty("assignees", out JsonElement assigneesArray) || assigneesArray.ValueKind != JsonValueKind.Array)
            return null;

        List<string> assignees = [];
        foreach (JsonElement assignee in assigneesArray.EnumerateArray())
        {
            string? login = assignee.GetProperty("login").GetString();
            if (!string.IsNullOrEmpty(login)) assignees.Add(login);
        }

        return assignees.Count > 0 ? string.Join(",", assignees) : null;
    }
}
