using System.Globalization;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Indexing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Controllers;

[ApiController]
[Route("api/v1")]
public class QueryController(JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpPost("query")]
    public IActionResult Query([FromBody] JiraQueryRequest request)
    {
        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        (string sql, List<SqliteParameter> parameters) = JiraQueryBuilder.Build(request);

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

        List<JiraIssueSummaryEntry> results = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = reader["Key"]?.ToString() ?? "";
            DateTimeOffset? updatedAt = null;
            if (reader["UpdatedAt"] is string updatedStr &&
                DateTimeOffset.TryParse(updatedStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset updated))
            {
                updatedAt = updated;
            }

            results.Add(new JiraIssueSummaryEntry
            {
                Key = key,
                ProjectKey = reader["ProjectKey"]?.ToString() ?? "",
                Title = reader["Title"]?.ToString() ?? "",
                Type = reader["Type"]?.ToString() ?? "",
                Status = reader["Status"]?.ToString() ?? "",
                Priority = reader["Priority"]?.ToString() ?? "",
                WorkGroup = reader["WorkGroup"]?.ToString() ?? "",
                Specification = reader["Specification"]?.ToString() ?? "",
                Url = $"{options.BaseUrl}/browse/{key}",
                UpdatedAt = updatedAt,
            });
        }

        return Ok(results);
    }

    [HttpGet("labels")]
    public IActionResult ListLabels()
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraIndexLabelRecord> labels = JiraIndexLabelRecord.SelectList(connection);
        return Ok(labels
            .Select(l => new { l.Name, l.IssueCount })
            .OrderByDescending(l => l.IssueCount));
    }
}