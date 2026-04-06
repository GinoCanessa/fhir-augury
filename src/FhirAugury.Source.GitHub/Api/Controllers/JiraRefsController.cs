using FhirAugury.Source.GitHub.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Api.Controllers;

[ApiController]
[Route("api/v1/items")]
public class JiraRefsController(GitHubDatabase db) : ControllerBase
{
    [HttpGet("jira-refs/{*key}")]
    public IActionResult GetJiraRefs([FromRoute] string? key, [FromQuery] string? repo, [FromQuery] string? jiraKey)
    {
        using SqliteConnection connection = db.OpenConnection();

        string sql = "SELECT SourceType, SourceId, RepoFullName, JiraKey, Context FROM github_jira_refs WHERE 1=1";
        List<SqliteParameter> parameters = [];

        if (!string.IsNullOrEmpty(repo))
        {
            sql += " AND RepoFullName = @repo";
            parameters.Add(new SqliteParameter("@repo", repo));
        }

        if (!string.IsNullOrEmpty(jiraKey))
        {
            sql += " AND JiraKey = @jiraKey";
            parameters.Add(new SqliteParameter("@jiraKey", jiraKey));
        }

        // If key is provided as the route parameter, filter by SourceId
        if (!string.IsNullOrEmpty(key))
        {
            sql += " AND SourceId = @sourceId";
            parameters.Add(new SqliteParameter("@sourceId", key));
        }

        sql += " ORDER BY JiraKey";

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

        List<object> refs = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            refs.Add(new
            {
                sourceType = reader.GetString(0),
                sourceId = reader.GetString(1),
                repoFullName = reader.GetString(2),
                jiraKey = reader.GetString(3),
                context = reader.IsDBNull(4) ? "" : reader.GetString(4),
            });
        }

        return Ok(new { jiraRefs = refs });
    }
}