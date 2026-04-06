using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Text;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Controllers;

[ApiController]
[Route("api/v1")]
public class SearchController(JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("search")]
    public IActionResult Search([FromQuery] string? q, [FromQuery] int? limit)
    {
        JiraServiceOptions options = optionsAccessor.Value;
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Query parameter 'q' is required" });

        using SqliteConnection connection = db.OpenConnection();
        string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(q);
        if (string.IsNullOrEmpty(ftsQuery))
            return Ok(new SearchResponse(q, 0, [], null));

        int maxResults = Math.Min(limit ?? 20, 200);

        string sql = """
            SELECT ji.Key, ji.Title,
                   snippet(jira_issues_fts, 1, '<b>', '</b>', '...', 20) as Snippet,
                   jira_issues_fts.rank, ji.Status, ji.UpdatedAt
            FROM jira_issues_fts
            JOIN jira_issues ji ON ji.Id = jira_issues_fts.rowid
            WHERE jira_issues_fts MATCH @query
            ORDER BY jira_issues_fts.rank
            LIMIT @limit
            """;

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", maxResults);

        List<SearchResult> results = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = reader.GetString(0);
            results.Add(new SearchResult
            {
                Source = SourceSystems.Jira,
                Id = key,
                Title = reader.GetString(1),
                Snippet = reader.IsDBNull(2) ? null : reader.GetString(2),
                Score = -reader.GetDouble(3),
                Url = $"{options.BaseUrl}/browse/{key}",
                UpdatedAt = JiraUrlHelper.ParseTimestamp(reader, 5),
                Metadata = new Dictionary<string, string>
                {
                    ["status"] = reader.IsDBNull(4) ? "" : reader.GetString(4),
                },
            });
        }

        return Ok(new SearchResponse(q, results.Count, results, null));
    }
}