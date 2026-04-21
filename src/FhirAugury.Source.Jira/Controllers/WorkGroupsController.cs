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

    /// <summary>
    /// Lists issues for a specific work group identified by its HL7 work group code
    /// (e.g. <c>fhir</c>, <c>pc</c>). The code is resolved against
    /// <c>hl7_workgroups.Code</c>, with <c>NameClean</c> accepted as an alternate so
    /// callers may also pass the cleaned-name form.
    /// </summary>
    [HttpGet("work-groups/{groupCode}/issues")]
    public IActionResult GetIssuesForWorkGroupCode(
        [FromRoute] string groupCode,
        [FromQuery] int? limit,
        [FromQuery] int? offset)
    {
        return QueryWorkGroupIssues(groupCode, groupName: null, limit, offset);
    }

    /// <summary>
    /// Lists issues filtered by an optional HL7 work group code and/or canonical
    /// work group name. When neither filter is supplied, returns all issues paged
    /// by <paramref name="limit"/> / <paramref name="offset"/>. When both are
    /// supplied, the filters are AND-ed together.
    /// </summary>
    [HttpGet("work-groups/issues")]
    public IActionResult GetIssuesForWorkGroup(
        [FromQuery] string? groupCode,
        [FromQuery] string? groupName,
        [FromQuery] int? limit,
        [FromQuery] int? offset)
    {
        return QueryWorkGroupIssues(groupCode, groupName, limit, offset);
    }

    private IActionResult QueryWorkGroupIssues(string? groupCode, string? groupName, int? limit, int? offset)
    {
        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        List<string> conditions = [];

        using SqliteCommand cmd = connection.CreateCommand();

        if (!string.IsNullOrWhiteSpace(groupCode))
        {
            // Resolve groupCode (HL7 Code, with NameClean as alternate) to the
            // canonical free-text Name persisted on jira_issues.WorkGroup. An
            // unknown code yields no matches (empty list), preserving the
            // historical "unknown group returns empty list" behaviour.
            string? resolvedFromCode = ResolveWorkGroupNameByCode(connection, groupCode);
            if (resolvedFromCode is null)
            {
                return Ok(new List<JiraIssueSummaryEntry>());
            }
            conditions.Add("WorkGroup = @code");
            cmd.Parameters.AddWithValue("@code", resolvedFromCode);
        }

        if (!string.IsNullOrWhiteSpace(groupName))
        {
            // groupName matches the canonical free-text WorkGroup name on the
            // issue. Accept either the index Name as authoritative or fall
            // through to direct equality on the raw value.
            string resolvedName = ResolveWorkGroupNameByName(connection, groupName) ?? groupName;
            conditions.Add("WorkGroup = @name");
            cmd.Parameters.AddWithValue("@name", resolvedName);
        }

        string sql = "SELECT Key, ProjectKey, Title, Type, Status, Priority, WorkGroup, Specification, UpdatedAt FROM jira_issues";
        if (conditions.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", conditions);
        }
        sql += " ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@limit", maxResults);
        cmd.Parameters.AddWithValue("@offset", skip);

        List<JiraIssueSummaryEntry> results = JiraUrlHelper.ReadIssueSummaries(cmd, options);
        return Ok(results);
    }

    private static string? ResolveWorkGroupNameByCode(SqliteConnection connection, string groupCode)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT iw.Name FROM jira_index_workgroups iw
              JOIN hl7_workgroups hwg ON hwg.Id = iw.WorkGroupId
             WHERE hwg.Code = @g
                OR hwg.NameClean = @g
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@g", groupCode);
        object? result = cmd.ExecuteScalar();
        return result is string s ? s : null;
    }

    private static string? ResolveWorkGroupNameByName(SqliteConnection connection, string groupName)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Name FROM jira_index_workgroups WHERE Name = @g LIMIT 1";
        cmd.Parameters.AddWithValue("@g", groupName);
        object? result = cmd.ExecuteScalar();
        return result is string s ? s : null;
    }
}