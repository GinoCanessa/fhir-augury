using FhirAugury.Database;
using FhirAugury.Database.Records;

namespace FhirAugury.Service.Api;

/// <summary>Jira-specific data access endpoints.</summary>
public static class JiraEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        var jira = group.MapGroup("/jira");

        jira.MapGet("/issues", ListIssues);
        jira.MapGet("/issues/{key}", GetIssue);
        jira.MapGet("/issues/{key}/comments", GetComments);
    }

    private static IResult ListIssues(
        DatabaseService dbService,
        HttpContext context)
    {
        var limitStr = context.Request.Query["limit"].FirstOrDefault();
        var offsetStr = context.Request.Query["offset"].FirstOrDefault();
        var limit = int.TryParse(limitStr, out var l) ? l : 50;
        var offset = int.TryParse(offsetStr, out var o) ? o : 0;

        var workGroup = context.Request.Query["work_group"].FirstOrDefault();
        var status = context.Request.Query["status"].FirstOrDefault();

        using var conn = dbService.OpenConnection();

        List<JiraIssueRecord> issues;
        if (!string.IsNullOrEmpty(workGroup))
        {
            issues = JiraIssueRecord.SelectList(conn, WorkGroup: workGroup);
        }
        else if (!string.IsNullOrEmpty(status))
        {
            issues = JiraIssueRecord.SelectList(conn, Status: status);
        }
        else
        {
            issues = JiraIssueRecord.SelectList(conn);
        }

        var paged = issues
            .OrderByDescending(i => i.UpdatedAt)
            .Skip(offset)
            .Take(limit)
            .Select(i => new
            {
                i.Key,
                i.Title,
                i.Status,
                i.Priority,
                i.WorkGroup,
                i.Specification,
                UpdatedAt = i.UpdatedAt.ToString("o"),
                i.CommentCount,
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
        string key,
        DatabaseService dbService)
    {
        using var conn = dbService.OpenConnection();
        var issue = JiraIssueRecord.SelectSingle(conn, Key: key);

        if (issue is null)
        {
            return Results.NotFound(new ProblemResponse("Not found", $"Issue '{key}' not found."));
        }

        return Results.Ok(issue);
    }

    private static IResult GetComments(
        string key,
        DatabaseService dbService)
    {
        using var conn = dbService.OpenConnection();
        var comments = JiraCommentRecord.SelectList(conn, IssueKey: key);
        return Results.Ok(comments);
    }
}
