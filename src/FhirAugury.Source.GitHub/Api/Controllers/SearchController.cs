using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Text;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Api.Controllers;

[ApiController]
[Route("api/v1")]
public class SearchController(GitHubDatabase db) : ControllerBase
{
    [HttpGet("search")]
    public IActionResult Search([FromQuery] string? q, [FromQuery] int? limit, [FromQuery] int? offset)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Query parameter 'q' is required" });

        using SqliteConnection connection = db.OpenConnection();
        string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(q);
        if (string.IsNullOrEmpty(ftsQuery))
            return Ok(new SearchResponse(q, 0, [], null));

        int maxResults = Math.Min(limit ?? 20, 200);
        int skip = Math.Max(offset ?? 0, 0);

        List<SearchResult> results = [];

        // Search issues
        string issueSql = """
            SELECT gi.UniqueKey, gi.Title,
                   snippet(github_issues_fts, 1, '<b>', '</b>', '...', 20) as Snippet,
                   github_issues_fts.rank, gi.State, gi.UpdatedAt
            FROM github_issues_fts
            JOIN github_issues gi ON gi.Id = github_issues_fts.rowid
            WHERE github_issues_fts MATCH @query
            ORDER BY github_issues_fts.rank
            LIMIT @limit OFFSET @offset
            """;

        using (SqliteCommand cmd = new SqliteCommand(issueSql, connection))
        {
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", maxResults);
            cmd.Parameters.AddWithValue("@offset", skip);

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string uniqueKey = reader.GetString(0);
                results.Add(new SearchResult
                {
                    Source = SourceSystems.GitHub,
                    Id = uniqueKey,
                    Title = reader.GetString(1),
                    Snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Score = -reader.GetDouble(3),
                    Url = GitHubUrlHelper.BuildIssueUrl(uniqueKey),
                    UpdatedAt = GitHubUrlHelper.ParseTimestamp(reader, 5),
                });
            }
        }

        // Search file contents
        string fileSql = """
            SELECT fc.RepoFullName, fc.FilePath,
                   snippet(github_file_contents_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                   github_file_contents_fts.rank,
                   fc.FileExtension, fc.ParserType
            FROM github_file_contents_fts
            JOIN github_file_contents fc ON fc.Id = github_file_contents_fts.rowid
            WHERE github_file_contents_fts MATCH @query
            ORDER BY github_file_contents_fts.rank
            LIMIT @limit OFFSET @offset
            """;

        using (SqliteCommand cmd = new SqliteCommand(fileSql, connection))
        {
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@limit", maxResults);
            cmd.Parameters.AddWithValue("@offset", skip);

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string repo = reader.GetString(0);
                string filePath = reader.GetString(1);
                string fileId = $"{repo}:{filePath}";

                results.Add(new SearchResult
                {
                    Source = SourceSystems.GitHub,
                    ContentType = ContentTypes.File,
                    Id = fileId,
                    Title = filePath,
                    Snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Score = -reader.GetDouble(3),
                    Url = $"https://github.com/{repo}/blob/main/{filePath}",
                });
            }
        }

        return Ok(new SearchResponse(q, results.Count, results, null));
    }

    [HttpGet("commits/search")]
    public IActionResult SearchCommits([FromQuery] string? q, [FromQuery] int? limit, [FromQuery] int? offset)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Query parameter 'q' is required" });

        using SqliteConnection connection = db.OpenConnection();
        string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(q);
        if (string.IsNullOrEmpty(ftsQuery))
            return Ok(new SearchResponse(q, 0, [], null));

        int maxResults = Math.Min(limit ?? 20, 200);
        int skip = Math.Max(offset ?? 0, 0);

        string sql = """
            SELECT gc.Sha, gc.Message, gc.Author, gc.Date, gc.Url,
                   github_commits_fts.rank
            FROM github_commits_fts
            JOIN github_commits gc ON gc.Id = github_commits_fts.rowid
            WHERE github_commits_fts MATCH @query
            ORDER BY github_commits_fts.rank
            LIMIT @limit OFFSET @offset
            """;

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", maxResults);
        cmd.Parameters.AddWithValue("@offset", skip);

        List<SearchResult> results = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SearchResult
            {
                Source = SourceSystems.GitHub,
                ContentType = ContentTypes.Commit,
                Id = reader.GetString(0),
                Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Score = -reader.GetDouble(5),
                Url = reader.IsDBNull(4) ? null : reader.GetString(4),
                UpdatedAt = GitHubUrlHelper.ParseTimestamp(reader, 3),
            });
        }

        return Ok(new SearchResponse(q, results.Count, results, null));
    }
}