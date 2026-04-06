using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Api.Controllers;

[ApiController]
[Route("api/v1/items")]
public class CommitsController(GitHubDatabase db) : ControllerBase
{
    [HttpGet("commits/{*key}")]
    public IActionResult GetCommits([FromRoute] string key)
    {
        using SqliteConnection connection = db.OpenConnection();
        (string repo, int number) = GitHubUrlHelper.ParseIssueKey(key);

        // Find commits via PR links
        List<GitHubCommitPrLinkRecord> prLinks = GitHubCommitPrLinkRecord.SelectList(connection,
            PrNumber: number, RepoFullName: repo);

        HashSet<string> writtenShas = [];
        List<object> commits = [];

        foreach (GitHubCommitPrLinkRecord link in prLinks)
        {
            if (!writtenShas.Add(link.CommitSha)) continue;
            GitHubCommitRecord? commit = GitHubCommitRecord.SelectSingle(connection, Sha: link.CommitSha);
            if (commit is not null)
                commits.Add(GitHubUrlHelper.MapCommitToJson(commit, connection));
        }

        // Also find commits mentioning this issue number in messages
        string issueRef = $"#{number}";
        using SqliteCommand cmd = new SqliteCommand(
            "SELECT Sha FROM github_commits WHERE RepoFullName = @repo AND Message LIKE @pattern LIMIT 100",
            connection);
        cmd.Parameters.AddWithValue("@repo", repo);
        cmd.Parameters.AddWithValue("@pattern", $"%{issueRef}%");

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string sha = reader.GetString(0);
            if (!writtenShas.Add(sha)) continue;
            GitHubCommitRecord? commit = GitHubCommitRecord.SelectSingle(connection, Sha: sha);
            if (commit is not null)
                commits.Add(GitHubUrlHelper.MapCommitToJson(commit, connection));
        }

        return Ok(new { key, commits });
    }
}