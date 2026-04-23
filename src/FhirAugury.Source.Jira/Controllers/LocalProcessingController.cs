using System.Globalization;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Indexing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Controllers;

/// <summary>
/// FR-02: local-processing workflow endpoints. Exposes the boolean
/// <c>ProcessedLocally</c> surface over the stored
/// <c>ProcessedLocallyAt</c> timestamp column on any of the four
/// Jira-issue-shaped tables.
/// </summary>
/// <remarks>
/// Endpoints accept an optional <c>?type=fhir|pss|baldef|ballot</c> query
/// parameter that selects the target table. Default is <c>fhir</c> (i.e.
/// <c>jira_issues</c>) for backward compatibility. Unknown types return
/// HTTP 400. <c>clear-all-processed</c> clears the selected table when
/// <c>type</c> is supplied; when omitted, it clears the flag across all
/// four tables.
/// </remarks>
[ApiController]
[Route("api/v1/local-processing")]
public class LocalProcessingController(
    JiraDatabase db,
    IOptions<JiraServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpPost("tickets")]
    public IActionResult GetTickets([FromBody] JiraLocalProcessingListRequest request, [FromQuery] string? type = null)
    {
        JiraLocalProcessingQueryBuilder.TableMapping? mapping = JiraLocalProcessingQueryBuilder.TryGetMapping(type);
        if (mapping is null) return BadRequest(new { error = $"Unknown type '{type}'. Expected one of: fhir, pss, baldef, ballot." });

        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();

        (string listSql, List<SqliteParameter> listParams) =
            JiraLocalProcessingQueryBuilder.BuildList(request, mapping);

        int limit = (request.Limit is null || request.Limit.Value <= 0)
            ? JiraLocalProcessingQueryBuilder.DefaultLimit
            : request.Limit.Value;
        int offset = (request.Offset is null || request.Offset.Value < 0)
            ? 0
            : request.Offset.Value;

        List<JiraIssueSummaryEntry> results = ReadSummaries(connection, listSql, listParams, options);

        (string countSql, List<SqliteParameter> countParams) =
            JiraLocalProcessingQueryBuilder.BuildCount(request, mapping);
        using SqliteCommand countCmd = new SqliteCommand(countSql, connection);
        foreach (SqliteParameter p in countParams) countCmd.Parameters.Add(p);
        int total = Convert.ToInt32(countCmd.ExecuteScalar());

        return Ok(new JiraLocalProcessingListResponse(results, limit, offset, total));
    }

    [HttpPost("random-ticket")]
    public IActionResult GetRandomTicket([FromBody] JiraLocalProcessingFilter request, [FromQuery] string? type = null)
    {
        JiraLocalProcessingQueryBuilder.TableMapping? mapping = JiraLocalProcessingQueryBuilder.TryGetMapping(type);
        if (mapping is null) return BadRequest(new { error = $"Unknown type '{type}'. Expected one of: fhir, pss, baldef, ballot." });

        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();

        (string sql, List<SqliteParameter> parameters) =
            JiraLocalProcessingQueryBuilder.BuildRandom(request, mapping);

        List<JiraIssueSummaryEntry> results = ReadSummaries(connection, sql, parameters, options);

        if (results.Count == 0) return NotFound();
        return Ok(results[0]);
    }

    [HttpPost("set-processed")]
    public IActionResult SetProcessed([FromBody] JiraLocalProcessingSetRequest request, [FromQuery] string? type = null)
    {
        if (string.IsNullOrWhiteSpace(request.Key))
        {
            return BadRequest(new { error = "Key is required" });
        }

        JiraLocalProcessingQueryBuilder.TableMapping? mapping = JiraLocalProcessingQueryBuilder.TryGetMapping(type);
        if (mapping is null) return BadRequest(new { error = $"Unknown type '{type}'. Expected one of: fhir, pss, baldef, ballot." });

        using SqliteConnection connection = db.OpenConnection();

        DateTimeOffset? existing;
        bool found = false;
        using (SqliteCommand selectCmd = connection.CreateCommand())
        {
            selectCmd.CommandText = $"SELECT ProcessedLocallyAt FROM {mapping.TableName} WHERE Key = @k";
            selectCmd.Parameters.Add(new SqliteParameter("@k", request.Key));
            using SqliteDataReader reader = selectCmd.ExecuteReader();
            existing = null;
            if (reader.Read())
            {
                found = true;
                object rawValue = reader.GetValue(0);
                if (rawValue is string s &&
                    DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
                {
                    existing = parsed;
                }
            }
        }

        if (!found) return NotFound();

        bool previousValue = ProcessedLocallyMapper.FromStorageValue(existing);
        object storageValue = ProcessedLocallyMapper.ToStorageValue(request.ProcessedLocally);

        using (SqliteCommand updateCmd = connection.CreateCommand())
        {
            updateCmd.CommandText = $"UPDATE {mapping.TableName} SET ProcessedLocallyAt = @v WHERE Key = @k";
            updateCmd.Parameters.Add(new SqliteParameter("@v", storageValue));
            updateCmd.Parameters.Add(new SqliteParameter("@k", request.Key));
            updateCmd.ExecuteNonQuery();
        }

        bool newValue = request.ProcessedLocally == true;
        return Ok(new JiraLocalProcessingSetResponse(request.Key, previousValue, newValue));
    }

    [HttpPost("clear-all-processed")]
    public IActionResult ClearAllProcessed([FromQuery] string? type = null)
    {
        using SqliteConnection connection = db.OpenConnection();

        IEnumerable<JiraLocalProcessingQueryBuilder.TableMapping> targets;
        if (string.IsNullOrWhiteSpace(type))
        {
            targets = JiraLocalProcessingQueryBuilder.AllMappings;
        }
        else
        {
            JiraLocalProcessingQueryBuilder.TableMapping? mapping = JiraLocalProcessingQueryBuilder.TryGetMapping(type);
            if (mapping is null) return BadRequest(new { error = $"Unknown type '{type}'. Expected one of: fhir, pss, baldef, ballot." });
            targets = [mapping];
        }

        int rowsAffected = 0;
        foreach (JiraLocalProcessingQueryBuilder.TableMapping t in targets)
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = $"UPDATE {t.TableName} SET ProcessedLocallyAt = NULL WHERE ProcessedLocallyAt IS NOT NULL";
            rowsAffected += cmd.ExecuteNonQuery();
        }
        return Ok(new JiraLocalProcessingClearResponse(rowsAffected));
    }

    private static List<JiraIssueSummaryEntry> ReadSummaries(
        SqliteConnection connection,
        string sql,
        List<SqliteParameter> parameters,
        JiraServiceOptions options)
    {
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

        return results;
    }
}
