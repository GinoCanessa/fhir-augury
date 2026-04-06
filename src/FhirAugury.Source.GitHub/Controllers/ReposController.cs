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

            result.Add(new
            {
                repo.FullName,
                repo.Description,
                issueCount,
                prCount,
                url = $"https://github.com/{repo.FullName}",
                repo.HasIssues,
            });
        }

        return Ok(new { repos = result });
    }
}