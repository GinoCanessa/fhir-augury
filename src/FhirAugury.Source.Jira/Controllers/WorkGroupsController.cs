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
public class WorkGroupsController(JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("work-groups")]
    public IActionResult ListWorkGroups()
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraIndexWorkGroupRecord> records = JiraIndexWorkGroupRecord.SelectList(connection);
        return Ok(records
            .Select(r => new { r.Name, r.IssueCount })
            .OrderByDescending(r => r.IssueCount));
    }

    [HttpGet("work-groups/{group}")]
    public IActionResult GetWorkGroupIssues([FromRoute] string group, [FromQuery] int? limit, [FromQuery] int? offset)
    {
        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        using SqliteCommand cmd = new SqliteCommand(
            "SELECT Key, ProjectKey, Title, Type, Status, Priority, WorkGroup, Specification, UpdatedAt FROM jira_issues WHERE WorkGroup = @wg ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset",
            connection);
        cmd.Parameters.AddWithValue("@wg", group);
        cmd.Parameters.AddWithValue("@limit", maxResults);
        cmd.Parameters.AddWithValue("@offset", skip);

        List<JiraIssueSummaryEntry> results = JiraUrlHelper.ReadIssueSummaries(cmd, options);
        return Ok(results);
    }
}