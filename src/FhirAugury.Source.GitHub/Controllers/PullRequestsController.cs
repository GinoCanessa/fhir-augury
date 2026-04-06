using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Controllers;

[ApiController]
[Route("api/v1/items")]
public class PullRequestsController(GitHubDatabase db) : ControllerBase
{
    [HttpGet("pr/{*key}")]
    public IActionResult GetPullRequest([FromRoute] string key)
    {
        using SqliteConnection connection = db.OpenConnection();
        (string repo, int number) = GitHubUrlHelper.ParseIssueKey(key);

        string uniqueKey = $"{repo}#{number}";
        GitHubIssueRecord? issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: uniqueKey);
        if (issue is null)
            return NotFound(new { error = $"PR {uniqueKey} not found" });
        if (!issue.IsPullRequest)
            return BadRequest(new { error = $"{uniqueKey} is not a pull request" });

        return Ok(new
        {
            issue.UniqueKey,
            issue.RepoFullName,
            issue.Number,
            issue.Title,
            issue.Body,
            issue.State,
            issue.Author,
            issue.Labels,
            issue.Assignees,
            issue.Milestone,
            issue.CreatedAt,
            issue.UpdatedAt,
            issue.ClosedAt,
            issue.MergeState,
            issue.HeadBranch,
            issue.BaseBranch,
            merged = issue.MergeState == "merged",
            url = GitHubUrlHelper.BuildIssueUrl(issue.UniqueKey),
        });
    }
}