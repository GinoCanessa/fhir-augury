using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Controllers;

[ApiController]
[Route("api/v1")]
public class ReposController(GitHubDatabase db) : ControllerBase
{
    [HttpGet("repos")]
    public IActionResult GetRepositories()
    {
        using SqliteConnection connection = db.OpenConnection();
        List<GitHubRepoRecord> repos = GitHubRepoRecord.SelectList(connection);

        List<object> result = [];
        foreach (GitHubRepoRecord repo in repos)
        {
            result.Add(BuildRepoPayload(connection, repo));
        }

        return Ok(new { repos = result });
    }

    [HttpGet("repos/{owner}/{name}")]
    public IActionResult GetRepository([FromRoute] string owner, [FromRoute] string name)
    {
        string fullName = $"{owner}/{name}";
        using SqliteConnection connection = db.OpenConnection();
        GitHubRepoRecord? repo = GitHubRepoRecord.SelectSingle(connection, FullName: fullName);
        if (repo is null)
            return NotFound(new { error = $"Repository {fullName} not found" });

        return Ok(BuildRepoPayload(connection, repo));
    }

    private static object BuildRepoPayload(SqliteConnection connection, GitHubRepoRecord repo)
    {
        int issueCount = 0, prCount = 0;
        using (SqliteCommand cmd = new SqliteCommand(
            "SELECT IsPullRequest, COUNT(*) FROM github_issues WHERE RepoFullName = @repo GROUP BY IsPullRequest",
            connection))
        {
            cmd.Parameters.AddWithValue("@repo", repo.FullName);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetBoolean(0))
                    prCount = reader.GetInt32(1);
                else
                    issueCount = reader.GetInt32(1);
            }
        }

        return new
        {
            repo.FullName,
            repo.Description,
            repo.Category,
            issueCount,
            prCount,
            url = $"https://github.com/{repo.FullName}",
            repo.HasIssues,
        };
    }
}