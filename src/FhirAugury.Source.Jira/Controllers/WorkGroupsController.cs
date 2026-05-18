using FhirAugury.Common.WorkGroups;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using FhirAugury.Common.Api;

namespace FhirAugury.Source.Jira.Controllers;

/// <summary>
/// Work-group rollup endpoints. Counts aggregate FHIR change requests
/// (<c>jira_issues</c>); PSS/BALDEF/BALLOT lifecycle columns are exposed
/// via the workgroup index but not from this controller.
/// </summary>
[ApiController]
[Route("api/v1")]
public class WorkGroupsController(
    JiraDatabase db,
    IOptions<JiraServiceOptions> optionsAccessor,
    WorkGroupResolverFactory resolverFactory) : ControllerBase
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
        bool anyMissingCode = false;
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            string name = r.GetString(0);
            string? code = r.IsDBNull(15) ? null : r.GetString(15);
            string? catalogNameClean = r.IsDBNull(17) ? null : r.GetString(17);
            string nameClean = catalogNameClean ?? Hl7WorkGroupNameCleaner.Clean(name);
            if (code is null) anyMissingCode = true;
            rows.Add(new JiraWorkGroupSummaryEntry
            {
                Name = name,
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
                WorkGroupCode = code,
                WorkGroupDefinition = r.IsDBNull(16) ? null : r.GetString(16),
                WorkGroupNameClean = nameClean,
                WorkGroupRetired = r.IsDBNull(18) ? null : r.GetBoolean(18),
            });
        }

        bool catalogJoinDegraded = rows.Count == 0 || anyMissingCode;
        return Ok(new JiraWorkGroupListResponse(catalogJoinDegraded, rows));
    }

    /// <summary>
    /// Lists issues for a specific work group identified by any of its
    /// canonical identifiers — HL7 <c>Code</c> (e.g. <c>fhir</c>, <c>pc</c>),
    /// display <c>Name</c> (e.g. <c>"Orders &amp; Observations"</c>), or
    /// PascalCase <c>NameClean</c> (e.g. <c>FHIRInfrastructure</c>). The
    /// route parameter is resolved through the shared
    /// <see cref="WorkGroupResolver"/> so callers may submit any of the
    /// three forms interchangeably. On <see cref="WorkGroupResolveOutcome.Ambiguous"/>
    /// returns 409 with the candidate list; on
    /// <see cref="WorkGroupResolveOutcome.NotFound"/> preserves the
    /// historical "unknown group → empty list" 200 response so callers
    /// (e.g. the index-prepared-db skill) do not need bespoke 404 handling.
    /// </summary>
    [HttpGet("work-groups/{groupCode}/issues")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult GetIssuesForWorkGroupCode(
        [FromRoute] string groupCode,
        [FromQuery] int? limit,
        [FromQuery] int? offset)
    {
        return QueryWorkGroupIssues(groupCode, groupName: null, limit, offset);
    }

    /// <summary>
    /// Lists issues filtered by an optional HL7 work group selector
    /// (<c>code</c>, <c>name</c>, or <c>nameClean</c> form) on either
    /// the <c>groupCode</c> or <c>groupName</c> query parameter, both
    /// routed through the shared <see cref="WorkGroupResolver"/>. When
    /// neither filter is supplied, returns all issues paged by
    /// <paramref name="limit"/> / <paramref name="offset"/>. When both
    /// are supplied, the filters are AND-ed together. On
    /// <see cref="WorkGroupResolveOutcome.Ambiguous"/> returns 409 with
    /// the candidate list.
    /// </summary>
    [HttpGet("work-groups/issues")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
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
        WorkGroupResolver resolver = resolverFactory.Create(connection);
        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        List<string> conditions = [];

        using SqliteCommand cmd = connection.CreateCommand();

        if (!string.IsNullOrWhiteSpace(groupCode))
        {
            WorkGroupResolveResult result = resolver.Resolve(groupCode);
            if (result.Outcome == WorkGroupResolveOutcome.Ambiguous)
            {
                return Conflict(BuildAmbiguousProblemDetails(result));
            }
            if (result.Outcome != WorkGroupResolveOutcome.Found)
            {
                return Ok(new List<JiraIssueSummaryEntry>());
            }
            conditions.Add("WorkGroup = @code");
            cmd.Parameters.AddWithValue("@code", result.Match!.Name);
        }

        if (!string.IsNullOrWhiteSpace(groupName))
        {
            WorkGroupResolveResult result = resolver.Resolve(groupName);
            string resolvedName = result.Outcome == WorkGroupResolveOutcome.Found
                ? result.Match!.Name
                : groupName;
            if (result.Outcome == WorkGroupResolveOutcome.Ambiguous)
            {
                return Conflict(BuildAmbiguousProblemDetails(result));
            }
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

    private static ProblemDetails BuildAmbiguousProblemDetails(WorkGroupResolveResult result) => new()
    {
        Title = "Ambiguous work-group selector",
        Detail = $"'{result.Input}' matched multiple work groups within the ambiguity delta. Refine using code or nameClean.",
        Status = StatusCodes.Status409Conflict,
        Extensions =
        {
            ["input"] = result.Input,
            ["score"] = result.Score,
            ["candidates"] = result.Candidates
                .Select(c => new { code = c.Dto.Code, name = c.Dto.Name, nameClean = c.Dto.NameClean, score = c.Score })
                .ToArray(),
        },
    };
}
