using System.Text;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Controllers;

/// <summary>
/// Read-only endpoints for Ballot Definition (BALDEF-*) Jira tickets
/// stored in <c>jira_baldef</c>. Minimal wrapper over the generated
/// <see cref="JiraBaldefRecord"/> accessors; see <c>ItemsController</c>
/// for the FHIR-change-request equivalent.
/// </summary>
[ApiController]
[Route("api/v1/baldef")]
public class BalDefController(
    JiraDatabase db,
    IOptions<JiraServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("{key}")]
    public IActionResult GetBalDef([FromRoute] string key)
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraBaldefRecord> matches = JiraBaldefRecord.SelectList(connection, Key: key);
        JiraBaldefRecord? record = matches.FirstOrDefault();
        if (record is null)
            return NotFound(new { error = $"BALDEF {key} not found" });

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
            record.BallotCode,
            record.BallotCycle,
            record.BallotPackageName,
            record.BallotCategory,
            record.Specification,
            record.SpecificationLocation,
            record.BallotOpens,
            record.BallotCloses,
            record.ProductFamily,
            record.ApprovalStatus,
            record.VotersTotalEligible,
            record.VotersAffirmative,
            record.VotersNegative,
            record.VotersAbstain,
            record.Reconciled,
            record.RelatedArtifacts,
            record.RelatedPages,
            Url = $"{options.BaseUrl}/browse/{record.Key}",
        });
    }

    [HttpGet]
    public IActionResult ListBalDef(
        [FromQuery] string? cycle,
        [FromQuery] string? level,
        [FromQuery] string? workGroup,
        [FromQuery] int? limit,
        [FromQuery] int? offset)
    {
        // NOTE: `workGroup` is accepted for API parity but BALDEF rows have no
        // direct work-group column; the filter is ignored. `level` maps to
        // `BallotCategory` (e.g. STU, Normative).
        _ = workGroup;

        using SqliteConnection connection = db.OpenConnection();
        JiraServiceOptions options = optionsAccessor.Value;

        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        StringBuilder sb = new StringBuilder(
            "SELECT Key, ProjectKey, Title, Status, Type, Priority, BallotCycle, BallotCategory, Specification, UpdatedAt FROM jira_baldef WHERE 1=1");
        List<SqliteParameter> parameters = [];
        if (!string.IsNullOrWhiteSpace(cycle))
        {
            sb.Append(" AND BallotCycle = @cycle");
            parameters.Add(new SqliteParameter("@cycle", cycle));
        }
        if (!string.IsNullOrWhiteSpace(level))
        {
            sb.Append(" AND BallotCategory = @lvl");
            parameters.Add(new SqliteParameter("@lvl", level));
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
                BallotCycle = reader.IsDBNull(6) ? null : reader.GetString(6),
                BallotCategory = reader.IsDBNull(7) ? null : reader.GetString(7),
                Specification = reader.IsDBNull(8) ? null : reader.GetString(8),
                UpdatedAt = JiraUrlHelper.ParseTimestamp(reader, 9),
                Url = $"{options.BaseUrl}/browse/{k}",
            });
        }

        return Ok(new { count = items.Count, items });
    }
}
