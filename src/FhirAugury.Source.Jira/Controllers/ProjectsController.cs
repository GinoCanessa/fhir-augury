using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Jira.Controllers;

/// <summary>
/// Read/update endpoints for the per-project ranking record. Mirrors
/// <c>StreamsController</c> in the Zulip source.
/// </summary>
[ApiController]
[Route("api/v1")]
public class ProjectsController(JiraDatabase db) : ControllerBase
{
    [HttpGet("projects")]
    public IActionResult ListProjects()
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraProjectRecord> projects = JiraProjectRecord.SelectList(connection);

        return Ok(new
        {
            total = projects.Count,
            projects = projects.Select(ToDto),
        });
    }

    [HttpGet("projects/{key}")]
    public IActionResult GetProject([FromRoute] string key)
    {
        using SqliteConnection connection = db.OpenConnection();
        JiraProjectRecord? project = JiraProjectRecord.SelectSingle(connection, Key: key);
        if (project is null)
            return NotFound(new { error = $"Project '{key}' not found" });

        return Ok(ToDto(project));
    }

    [HttpPut("projects/{key}")]
    public IActionResult UpdateProject([FromRoute] string key, [FromBody] JiraProjectUpdateRequest body)
    {
        using SqliteConnection connection = db.OpenConnection();
        JiraProjectRecord? project = JiraProjectRecord.SelectSingle(connection, Key: key);
        if (project is null)
            return NotFound(new { error = $"Project '{key}' not found" });

        project.Enabled = body.Enabled;
        if (body.BaselineValue.HasValue)
            project.BaselineValue = Math.Clamp(body.BaselineValue.Value, 0, 10);

        JiraProjectRecord.Update(connection, project);

        return Ok(ToDto(project));
    }

    private static object ToDto(JiraProjectRecord p) => new
    {
        p.Key,
        p.Enabled,
        p.BaselineValue,
        p.IssueCount,
        p.LastSyncAt,
    };
}
