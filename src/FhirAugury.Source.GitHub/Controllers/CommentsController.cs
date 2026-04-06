using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Controllers;

[ApiController]
[Route("api/v1/items")]
public class CommentsController(GitHubDatabase db) : ControllerBase
{
    [HttpGet("comments/{*key}")]
    public IActionResult GetComments([FromRoute] string key)
    {
        using SqliteConnection connection = db.OpenConnection();
        (string repo, int number) = GitHubUrlHelper.ParseIssueKey(key);

        List<GitHubCommentRecord> comments = GitHubCommentRecord.SelectList(connection,
            RepoFullName: repo, IssueNumber: number);

        List<CommentInfo> result = comments.Select(c => new CommentInfo(
            c.Id.ToString(), c.Author, c.Body, c.CreatedAt,
            $"https://github.com/{c.RepoFullName}/issues/{c.IssueNumber}#issuecomment-{c.Id}")).ToList();

        return Ok(new { key, comments = result });
    }
}