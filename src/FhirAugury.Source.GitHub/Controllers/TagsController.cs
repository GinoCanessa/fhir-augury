using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Controllers;

[ApiController]
[Route("api/v1")]
public class TagsController(GitHubDatabase db) : ControllerBase
{
    /// <summary>
    /// Lists all tags for a repository, with file counts and weights.
    /// </summary>
    [HttpGet("repos/{owner}/{name}/tags")]
    public IActionResult ListTags(
        [FromRoute] string owner, [FromRoute] string name,
        [FromQuery] string? category, [FromQuery] int? limit)
    {
        string repoFullName = $"{owner}/{name}";
        int maxResults = Math.Min(limit ?? 500, 2000);

        using SqliteConnection connection = db.OpenConnection();

        string sql = """
            SELECT TagCategory, TagName, TagModifier, COUNT(*) as FileCount, AVG(Weight) as AvgWeight
            FROM github_file_tags
            WHERE RepoFullName = @repo
            """;

        if (!string.IsNullOrEmpty(category))
            sql += " AND TagCategory = @category";

        sql += """
             GROUP BY TagCategory, TagName, TagModifier
             ORDER BY FileCount DESC
             LIMIT @limit
            """;

        using SqliteCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("@repo", repoFullName);
        cmd.Parameters.AddWithValue("@limit", maxResults);
        if (!string.IsNullOrEmpty(category))
            cmd.Parameters.AddWithValue("@category", category);

        List<object> tags = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            tags.Add(new
            {
                category = reader.GetString(0),
                name = reader.GetString(1),
                modifier = reader.IsDBNull(2) ? null : reader.GetString(2),
                fileCount = reader.GetInt32(3),
                weight = reader.GetDouble(4),
            });
        }

        return Ok(new { repo = repoFullName, total = tags.Count, tags });
    }

    /// <summary>
    /// Lists files matching a specific tag.
    /// </summary>
    [HttpGet("repos/{owner}/{name}/tags/files")]
    public IActionResult ListTaggedFiles(
        [FromRoute] string owner, [FromRoute] string name,
        [FromQuery] string? category, [FromQuery] string? tagName,
        [FromQuery] string? modifier, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(category) && string.IsNullOrWhiteSpace(tagName))
            return BadRequest(new { error = "At least one of 'category' or 'tagName' is required" });

        string repoFullName = $"{owner}/{name}";
        int maxResults = Math.Min(limit ?? 100, 1000);

        using SqliteConnection connection = db.OpenConnection();

        string sql = "SELECT FilePath, TagCategory, TagName, TagModifier, Weight FROM github_file_tags WHERE RepoFullName = @repo";

        if (!string.IsNullOrEmpty(category))
            sql += " AND TagCategory = @category";
        if (!string.IsNullOrEmpty(tagName))
            sql += " AND TagName = @tagName";
        if (!string.IsNullOrEmpty(modifier))
            sql += " AND TagModifier = @modifier";

        sql += " ORDER BY Weight DESC, FilePath LIMIT @limit";

        using SqliteCommand cmd = new(sql, connection);
        cmd.Parameters.AddWithValue("@repo", repoFullName);
        cmd.Parameters.AddWithValue("@limit", maxResults);
        if (!string.IsNullOrEmpty(category))
            cmd.Parameters.AddWithValue("@category", category);
        if (!string.IsNullOrEmpty(tagName))
            cmd.Parameters.AddWithValue("@tagName", tagName);
        if (!string.IsNullOrEmpty(modifier))
            cmd.Parameters.AddWithValue("@modifier", modifier);

        List<object> files = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            files.Add(new
            {
                filePath = reader.GetString(0),
                category = reader.GetString(1),
                name = reader.GetString(2),
                modifier = reader.IsDBNull(3) ? null : reader.GetString(3),
                weight = reader.GetDouble(4),
            });
        }

        return Ok(new { repo = repoFullName, total = files.Count, files });
    }

    /// <summary>
    /// Searches file contents filtered by tag, joining github_file_tags with
    /// github_file_contents for tag-scoped FTS5 search.
    /// </summary>
    [HttpGet("repos/{owner}/{name}/tags/search")]
    public IActionResult SearchByTag(
        [FromRoute] string owner, [FromRoute] string name,
        [FromQuery] string? query, [FromQuery] string? category,
        [FromQuery] string? tagName, [FromQuery] int? limit)
    {
        if (string.IsNullOrWhiteSpace(category) && string.IsNullOrWhiteSpace(tagName))
            return BadRequest(new { error = "At least one of 'category' or 'tagName' is required" });

        string repoFullName = $"{owner}/{name}";
        int maxResults = Math.Min(limit ?? 50, 200);

        using SqliteConnection connection = db.OpenConnection();

        // Build query - if FTS query provided, join with FTS; otherwise just list tagged files
        if (!string.IsNullOrWhiteSpace(query))
        {
            string ftsQuery = FhirAugury.Common.Text.FtsQueryHelper.SanitizeFtsQuery(query);
            if (string.IsNullOrEmpty(ftsQuery))
                return Ok(new { repo = repoFullName, total = 0, hits = Array.Empty<object>() });

            string sql = """
                SELECT fc.FilePath, fc.FileExtension, fc.ParserType, fc.ContentLength,
                       MAX(ft.Weight) as TagWeight,
                       snippet(github_file_contents_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                       github_file_contents_fts.rank as FtsRank
                FROM github_file_contents_fts
                JOIN github_file_contents fc ON fc.Id = github_file_contents_fts.rowid
                JOIN github_file_tags ft ON fc.RepoFullName = ft.RepoFullName AND fc.FilePath = ft.FilePath
                WHERE github_file_contents_fts MATCH @query
                  AND fc.RepoFullName = @repo
                """;

            if (!string.IsNullOrEmpty(category))
                sql += " AND ft.TagCategory = @category";
            if (!string.IsNullOrEmpty(tagName))
                sql += " AND ft.TagName = @tagName";

            sql += """
                 GROUP BY fc.FilePath
                 ORDER BY TagWeight DESC, github_file_contents_fts.rank
                 LIMIT @limit
                """;

            using SqliteCommand cmd = new(sql, connection);
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.Parameters.AddWithValue("@limit", maxResults);
            if (!string.IsNullOrEmpty(category))
                cmd.Parameters.AddWithValue("@category", category);
            if (!string.IsNullOrEmpty(tagName))
                cmd.Parameters.AddWithValue("@tagName", tagName);

            List<object> hits = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                hits.Add(new
                {
                    filePath = reader.GetString(0),
                    fileExtension = reader.GetString(1),
                    parserType = reader.GetString(2),
                    contentLength = reader.GetInt32(3),
                    tagWeight = reader.GetDouble(4),
                    snippet = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ftsScore = -reader.GetDouble(6),
                });
            }

            return Ok(new { repo = repoFullName, total = hits.Count, hits });
        }
        else
        {
            // No FTS query — just list tagged files with content info
            string sql = """
                SELECT fc.FilePath, fc.FileExtension, fc.ParserType, fc.ContentLength,
                       MAX(ft.Weight) as TagWeight
                FROM github_file_tags ft
                JOIN github_file_contents fc ON fc.RepoFullName = ft.RepoFullName AND fc.FilePath = ft.FilePath
                WHERE ft.RepoFullName = @repo
                """;

            if (!string.IsNullOrEmpty(category))
                sql += " AND ft.TagCategory = @category";
            if (!string.IsNullOrEmpty(tagName))
                sql += " AND ft.TagName = @tagName";

            sql += """
                 GROUP BY fc.FilePath
                 ORDER BY TagWeight DESC, fc.FilePath
                 LIMIT @limit
                """;

            using SqliteCommand cmd = new(sql, connection);
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.Parameters.AddWithValue("@limit", maxResults);
            if (!string.IsNullOrEmpty(category))
                cmd.Parameters.AddWithValue("@category", category);
            if (!string.IsNullOrEmpty(tagName))
                cmd.Parameters.AddWithValue("@tagName", tagName);

            List<object> hits = [];
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                hits.Add(new
                {
                    filePath = reader.GetString(0),
                    fileExtension = reader.GetString(1),
                    parserType = reader.GetString(2),
                    contentLength = reader.GetInt32(3),
                    tagWeight = reader.GetDouble(4),
                });
            }

            return Ok(new { repo = repoFullName, total = hits.Count, hits });
        }
    }
}
