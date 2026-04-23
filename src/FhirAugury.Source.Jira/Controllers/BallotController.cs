using System.Text;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Controllers;

/// <summary>
/// Read-only endpoints for Ballot vote (BALLOT-*) Jira tickets stored in
/// <c>jira_ballot</c>. Minimal wrapper over the generated
/// <see cref="JiraBallotRecord"/> accessors; see <c>ItemsController</c>
/// for the FHIR-change-request equivalent.
/// </summary>
[ApiController]
[Route("api/v1/ballot")]
public class BallotController(
    JiraDatabase db,
    IOptions<JiraServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("{key}")]
    public IActionResult GetBallot([FromRoute] string key)
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraBallotRecord> matches = JiraBallotRecord.SelectList(connection, Key: key);
        JiraBallotRecord? record = matches.FirstOrDefault();
        if (record is null)
            return NotFound(new { error = $"BALLOT {key} not found" });

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
            record.VoteBallot,
            record.VoteItem,
            record.ExternalId,
            record.Organization,
            record.OrganizationCategory,
            record.BallotCategory,
            record.VoteSameAs,
            record.Specification,
            record.Reconciled,
            record.BallotPackageCode,
            record.Voter,
            record.BallotCycle,
            record.RelatedFhirIssue,
            Url = $"{options.BaseUrl}/browse/{record.Key}",
        });
    }

    [HttpGet]
    public IActionResult ListBallot(
        [FromQuery] string? cycle,
        [FromQuery] string? specification,
        [FromQuery] string? disposition,
        [FromQuery] int? limit,
        [FromQuery] int? offset)
    {
        // NOTE: `disposition` maps to Status (e.g. open / resolved) — BALLOT
        // rows do not carry a dedicated disposition column. TODO: if callers
        // need the FHIR-side disposition of the linked change request,
        // resolve through `RelatedFhirIssue` into `jira_issues`.
        using SqliteConnection connection = db.OpenConnection();
        JiraServiceOptions options = optionsAccessor.Value;

        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        StringBuilder sb = new StringBuilder(
            "SELECT Key, ProjectKey, Title, Status, Type, Priority, BallotCycle, Specification, Organization, RelatedFhirIssue, UpdatedAt FROM jira_ballot WHERE 1=1");
        List<SqliteParameter> parameters = [];
        if (!string.IsNullOrWhiteSpace(cycle))
        {
            sb.Append(" AND BallotCycle = @cycle");
            parameters.Add(new SqliteParameter("@cycle", cycle));
        }
        if (!string.IsNullOrWhiteSpace(specification))
        {
            sb.Append(" AND Specification = @spec");
            parameters.Add(new SqliteParameter("@spec", specification));
        }
        if (!string.IsNullOrWhiteSpace(disposition))
        {
            sb.Append(" AND Status = @disp");
            parameters.Add(new SqliteParameter("@disp", disposition));
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
                Specification = reader.IsDBNull(7) ? null : reader.GetString(7),
                Organization = reader.IsDBNull(8) ? null : reader.GetString(8),
                RelatedFhirIssue = reader.IsDBNull(9) ? null : reader.GetString(9),
                UpdatedAt = JiraUrlHelper.ParseTimestamp(reader, 10),
                Url = $"{options.BaseUrl}/browse/{k}",
            });
        }

        return Ok(new { count = items.Count, items });
    }
}
