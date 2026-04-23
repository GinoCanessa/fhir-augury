using System.Text;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Controllers;

/// <summary>
/// Read-only endpoints for Project Scope Statement (PSS-*) Jira tickets
/// stored in <c>jira_pss</c>. Minimal wrapper over the generated
/// <see cref="JiraProjectScopeStatementRecord"/> accessors; see
/// <c>ItemsController</c> for the FHIR-change-request equivalent.
/// </summary>
[ApiController]
[Route("api/v1/pss")]
public class ProjectScopeStatementController(
    JiraDatabase db,
    IOptions<JiraServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("{key}")]
    public IActionResult GetPss([FromRoute] string key)
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraProjectScopeStatementRecord> matches =
            JiraProjectScopeStatementRecord.SelectList(connection, Key: key);
        JiraProjectScopeStatementRecord? record = matches.FirstOrDefault();
        if (record is null)
            return NotFound(new { error = $"PSS {key} not found" });

        JiraServiceOptions options = optionsAccessor.Value;
        return Ok(new
        {
            record.Key,
            record.ProjectKey,
            record.Title,
            record.Status,
            record.Type,
            record.Priority,
            record.Assignee,
            record.Reporter,
            record.CreatedAt,
            record.UpdatedAt,
            record.ResolvedAt,
            record.SponsoringWorkGroup,
            record.SponsoringWorkGroupsLegacy,
            record.CoSponsoringWorkGroups,
            record.Realm,
            record.SteeringDivision,
            record.ProductFamily,
            record.BallotCycleTarget,
            record.ApprovalDate,
            record.RejectionDate,
            record.OptOutDate,
            record.ProjectCommonName,
            record.ProjectDescription,
            record.ProjectNeed,
            record.ProjectFacilitator,
            record.NormativeNotification,
            Url = $"{options.BaseUrl}/browse/{record.Key}",
        });
    }

    [HttpGet]
    public IActionResult ListPss(
        [FromQuery] string? workGroup,
        [FromQuery] string? status,
        [FromQuery] int? limit,
        [FromQuery] int? offset)
    {
        using SqliteConnection connection = db.OpenConnection();
        JiraServiceOptions options = optionsAccessor.Value;

        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        StringBuilder sb = new StringBuilder(
            "SELECT Key, ProjectKey, Title, Status, Type, Priority, SponsoringWorkGroup, SponsoringWorkGroupsLegacy, UpdatedAt FROM jira_pss WHERE 1=1");
        List<SqliteParameter> parameters = [];
        if (!string.IsNullOrWhiteSpace(workGroup))
        {
            sb.Append(" AND (SponsoringWorkGroup = @wg OR SponsoringWorkGroupsLegacy = @wg)");
            parameters.Add(new SqliteParameter("@wg", workGroup));
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            sb.Append(" AND Status = @st");
            parameters.Add(new SqliteParameter("@st", status));
        }
        sb.Append(" ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset");
        parameters.Add(new SqliteParameter("@limit", maxResults));
        parameters.Add(new SqliteParameter("@offset", skip));

        using SqliteCommand cmd = new SqliteCommand(sb.ToString(), connection);
        foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

        List<object> items = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string k = reader.GetString(0);
            items.Add(new
            {
                Key = k,
                ProjectKey = reader.GetString(1),
                Title = reader.GetString(2),
                Status = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Type = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Priority = reader.IsDBNull(5) ? "" : reader.GetString(5),
                SponsoringWorkGroup = reader.IsDBNull(6) ? null : reader.GetString(6),
                SponsoringWorkGroupsLegacy = reader.IsDBNull(7) ? null : reader.GetString(7),
                UpdatedAt = JiraUrlHelper.ParseTimestamp(reader, 8),
                Url = $"{options.BaseUrl}/browse/{k}",
            });
        }

        return Ok(new { count = items.Count, items });
    }
}
