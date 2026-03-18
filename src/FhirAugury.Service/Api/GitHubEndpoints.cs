using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Indexing;

namespace FhirAugury.Service.Api;

/// <summary>GitHub-specific data access endpoints.</summary>
public static class GitHubEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        var github = group.MapGroup("/github");

        github.MapGet("/issues", ListIssues);
        github.MapGet("/issues/{id}", GetIssue);
    }

    private static IResult ListIssues(
        DatabaseService dbService,
        HttpContext context)
    {
        var limitStr = context.Request.Query["limit"].FirstOrDefault();
        var offsetStr = context.Request.Query["offset"].FirstOrDefault();
        var limit = int.TryParse(limitStr, out var l) ? l : 50;
        var offset = int.TryParse(offsetStr, out var o) ? o : 0;

        var repo = context.Request.Query["repo"].FirstOrDefault();
        var state = context.Request.Query["state"].FirstOrDefault();
        var query = context.Request.Query["q"].FirstOrDefault();

        using var conn = dbService.OpenConnection();

        if (!string.IsNullOrEmpty(query))
        {
            var results = FtsSearchService.SearchGitHubIssues(conn, query, limit, repo, state);
            return Results.Ok(results);
        }

        List<GitHubIssueRecord> issues;
        if (!string.IsNullOrEmpty(repo))
        {
            issues = GitHubIssueRecord.SelectList(conn, RepoFullName: repo);
        }
        else if (!string.IsNullOrEmpty(state))
        {
            issues = GitHubIssueRecord.SelectList(conn, State: state);
        }
        else
        {
            issues = GitHubIssueRecord.SelectList(conn);
        }

        var paged = issues
            .OrderByDescending(i => i.UpdatedAt)
            .Skip(offset)
            .Take(limit)
            .Select(i => new
            {
                i.UniqueKey,
                i.RepoFullName,
                i.Number,
                i.IsPullRequest,
                i.Title,
                i.State,
                i.Author,
                i.Labels,
                UpdatedAt = i.UpdatedAt.ToString("o"),
            });

        return Results.Ok(new
        {
            Total = issues.Count,
            Offset = offset,
            Limit = limit,
            Items = paged,
        });
    }

    private static IResult GetIssue(
        string id,
        DatabaseService dbService)
    {
        using var conn = dbService.OpenConnection();
        var issue = GitHubIssueRecord.SelectSingle(conn, UniqueKey: id);

        if (issue is null)
        {
            return Results.NotFound(new ProblemResponse("Not found", $"Issue '{id}' not found."));
        }

        var comments = GitHubCommentRecord.SelectList(conn, RepoFullName: issue.RepoFullName, IssueNumber: issue.Number);

        return Results.Ok(new
        {
            Issue = issue,
            Comments = comments,
        });
    }
}
