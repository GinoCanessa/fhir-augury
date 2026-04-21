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
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT iw.Name, iw.IssueCount,
                   iw.IssueCountSubmitted, iw.IssueCountTriaged, iw.IssueCountWaitingForInput,
                   iw.IssueCountNoChange,  iw.IssueCountChangeRequired,
                   iw.IssueCountPublished, iw.IssueCountApplied, iw.IssueCountDuplicate,
                   iw.IssueCountClosed,    iw.IssueCountBalloted,
                   iw.IssueCountWithdrawn, iw.IssueCountDeferred, iw.IssueCountOther,
                   hwg.Code, hwg.Definition, hwg.NameClean, hwg.Retired
              FROM jira_index_workgroups iw
              LEFT JOIN hl7_workgroups   hwg ON hwg.Id = iw.WorkGroupId
             ORDER BY iw.IssueCount DESC, iw.Name ASC
            """;

        List<JiraWorkGroupSummaryEntry> rows = [];
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new JiraWorkGroupSummaryEntry
            {
                Name = r.GetString(0),
                IssueCount = r.GetInt32(1),
                IssueCountSubmitted = r.GetInt32(2),
                IssueCountTriaged = r.GetInt32(3),
                IssueCountWaitingForInput = r.GetInt32(4),
                IssueCountNoChange = r.GetInt32(5),
                IssueCountChangeRequired = r.GetInt32(6),
                IssueCountPublished = r.GetInt32(7),
                IssueCountApplied = r.GetInt32(8),
                IssueCountDuplicate = r.GetInt32(9),
                IssueCountClosed = r.GetInt32(10),
                IssueCountBalloted = r.GetInt32(11),
                IssueCountWithdrawn = r.GetInt32(12),
                IssueCountDeferred = r.GetInt32(13),
                IssueCountOther = r.GetInt32(14),
                WorkGroupCode = r.IsDBNull(15) ? null : r.GetString(15),
                WorkGroupDefinition = r.IsDBNull(16) ? null : r.GetString(16),
                WorkGroupNameClean = r.IsDBNull(17) ? null : r.GetString(17),
                WorkGroupRetired = r.IsDBNull(18) ? null : r.GetBoolean(18),
            });
        }
        return Ok(rows);
    }

    [HttpGet("work-groups/{group}")]
    public IActionResult GetWorkGroupIssues([FromRoute] string group, [FromQuery] int? limit, [FromQuery] int? offset)
    {
        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        // Resolve {group} against the canonical free-text Name on
        // jira_index_workgroups; accept the HL7 Code or NameClean as
        // alternates. If nothing matches, fall back to the raw value so the
        // existing "unknown group returns empty list" behaviour is preserved.
        string resolvedName = ResolveWorkGroupName(connection, group) ?? group;

        using SqliteCommand cmd = new SqliteCommand(
            "SELECT Key, ProjectKey, Title, Type, Status, Priority, WorkGroup, Specification, UpdatedAt FROM jira_issues WHERE WorkGroup = @wg ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset",
            connection);
        cmd.Parameters.AddWithValue("@wg", resolvedName);
        cmd.Parameters.AddWithValue("@limit", maxResults);
        cmd.Parameters.AddWithValue("@offset", skip);

        List<JiraIssueSummaryEntry> results = JiraUrlHelper.ReadIssueSummaries(cmd, options);
        return Ok(results);
    }

    private static string? ResolveWorkGroupName(SqliteConnection connection, string group)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT iw.Name FROM jira_index_workgroups iw
              LEFT JOIN hl7_workgroups hwg ON hwg.Id = iw.WorkGroupId
             WHERE iw.Name = @g
                OR hwg.Code = @g
                OR hwg.NameClean = @g
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@g", group);
        object? result = cmd.ExecuteScalar();
        return result is string s ? s : null;
    }
}