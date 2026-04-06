using FhirAugury.Common.Text;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Controllers;

[ApiController]
[Route("api/v1")]
public class SearchController(ConfluenceDatabase db, IOptions<ConfluenceServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("search")]
    public IActionResult Search([FromQuery] string? q, [FromQuery] int? limit)
    {
        ConfluenceServiceOptions options = optionsAccessor.Value;
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Query parameter 'q' is required" });

        using SqliteConnection connection = db.OpenConnection();
        string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(q);
        if (string.IsNullOrEmpty(ftsQuery))
            return Ok(new { query = q, results = Array.Empty<object>() });

        int maxResults = Math.Min(limit ?? 20, 200);

        string sql = """
            SELECT cp.ConfluenceId, cp.Title,
                   snippet(confluence_pages_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                   confluence_pages_fts.rank, cp.SpaceKey, cp.LastModifiedAt
            FROM confluence_pages_fts
            JOIN confluence_pages cp ON cp.Id = confluence_pages_fts.rowid
            WHERE confluence_pages_fts MATCH @query
            ORDER BY confluence_pages_fts.rank
            LIMIT @limit
            """;

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", maxResults);

        List<object> results = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string pageId = reader.GetString(0);
            results.Add(new
            {
                pageId,
                title = reader.GetString(1),
                snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                score = -reader.GetDouble(3),
                spaceKey = reader.IsDBNull(4) ? null : reader.GetString(4),
                url = $"{options.BaseUrl}/pages/{pageId}",
            });
        }

        return Ok(new { query = q, total = results.Count, results });
    }
}