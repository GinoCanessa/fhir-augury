using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Controllers;

[ApiController]
[Route("api/v1")]
public class SpecificationsController(JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("specifications")]
    public IActionResult ListSpecifications()
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraIndexSpecificationRecord> records = JiraIndexSpecificationRecord.SelectList(connection);
        return Ok(records
            .Select(r => new { r.Name, r.IssueCount })
            .OrderByDescending(r => r.IssueCount));
    }

    [HttpGet("specifications/{spec}")]
    public IActionResult GetSpecificationIssues([FromRoute] string spec, [FromQuery] int? limit, [FromQuery] int? offset)
    {
        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        using SqliteCommand cmd = new SqliteCommand(
            "SELECT Key, ProjectKey, Title, Type, Status, Priority, WorkGroup, Specification, UpdatedAt FROM jira_issues WHERE Specification = @spec ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset",
            connection);
        cmd.Parameters.AddWithValue("@spec", spec);
        cmd.Parameters.AddWithValue("@limit", maxResults);
        cmd.Parameters.AddWithValue("@offset", skip);

        List<JiraIssueSummaryEntry> results = JiraUrlHelper.ReadIssueSummaries(cmd, options);
        return Ok(results);
    }

    [HttpGet("spec-artifacts")]
    public IActionResult GetSpecArtifacts([FromQuery] string? family)
    {
        using SqliteConnection connection = db.OpenConnection();
        string sql = "SELECT Family, SpecKey, SpecName, GitUrl, PublishedUrl, DefaultWorkgroup FROM jira_spec_artifacts";
        if (!string.IsNullOrEmpty(family))
            sql += " WHERE Family = @family";
        sql += " ORDER BY Family, SpecKey";

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        if (!string.IsNullOrEmpty(family))
            cmd.Parameters.AddWithValue("@family", family);

        List<SpecArtifactEntry> results = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SpecArtifactEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return Ok(results);
    }

    [HttpGet("issue-numbers")]
    public IActionResult GetIssueNumbers([FromQuery] string? project)
    {
        using SqliteConnection connection = db.OpenConnection();
        string sql = "SELECT Key FROM jira_issues";
        if (!string.IsNullOrEmpty(project))
            sql += " WHERE ProjectKey = @project";

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        if (!string.IsNullOrEmpty(project))
            cmd.Parameters.AddWithValue("@project", project);

        List<int> issueNumbers = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = reader.GetString(0);
            int dashIndex = key.LastIndexOf('-');
            if (dashIndex >= 0 && int.TryParse(key.AsSpan(dashIndex + 1), out int number))
                issueNumbers.Add(number);
        }

        return Ok(new IssueNumbersResponse(issueNumbers));
    }
}