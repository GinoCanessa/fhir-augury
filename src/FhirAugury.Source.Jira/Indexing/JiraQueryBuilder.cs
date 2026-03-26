using System.Text;
using FhirAugury.Common.Text;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Jira.Indexing;

/// <summary>
/// Builds parameterized SQL queries from JiraQueryRequest fields.
/// All fields are optional; combined with AND. Repeated values within a field use IN (OR).
/// </summary>
public static class JiraQueryBuilder
{
    private static readonly HashSet<string> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "title", "status", "priority", "type", "created_at", "updated_at",
        "resolved_at", "work_group", "specification", "project_key", "assignee", "reporter",
    };

    /// <summary>
    /// Builds a SELECT query with parameterized WHERE clause from the given query request.
    /// Returns the SQL string and a list of parameters to bind.
    /// </summary>
    public static (string Sql, List<SqliteParameter> Parameters) Build(Fhiraugury.JiraQueryRequest request)
    {
        StringBuilder sb = new StringBuilder("SELECT * FROM jira_issues WHERE 1=1");
        List<SqliteParameter> parameters = new List<SqliteParameter>();
        int paramIdx = 0;

        AddInClause(sb, parameters, "Status", request.Statuses, ref paramIdx);
        AddInClause(sb, parameters, "Resolution", request.Resolutions, ref paramIdx);
        AddInClause(sb, parameters, "WorkGroup", request.WorkGroups, ref paramIdx);
        AddInClause(sb, parameters, "Specification", request.Specifications, ref paramIdx);
        AddInClause(sb, parameters, "ProjectKey", request.Projects, ref paramIdx);
        AddNotInClause(sb, parameters, "ProjectKey", request.ExcludeProjects, ref paramIdx);
        AddInClause(sb, parameters, "Type", request.Types_, ref paramIdx);
        AddInClause(sb, parameters, "Priority", request.Priorities, ref paramIdx);
        AddInClause(sb, parameters, "Assignee", request.Assignees, ref paramIdx);
        AddInClause(sb, parameters, "Reporter", request.Reporters, ref paramIdx);

        // Labels use LIKE matching since labels are comma-separated
        foreach (string? label in request.Labels)
        {
            string name = $"@lbl{paramIdx++}";
            sb.Append($" AND Labels LIKE {name}");
            parameters.Add(new SqliteParameter(name, $"%{label}%"));
        }

        // Timestamp filters
        if (request.CreatedAfter is not null)
        {
            string name = $"@p{paramIdx++}";
            sb.Append($" AND CreatedAt >= {name}");
            parameters.Add(new SqliteParameter(name, request.CreatedAfter.ToDateTimeOffset().ToString("o")));
        }

        if (request.CreatedBefore is not null)
        {
            string name = $"@p{paramIdx++}";
            sb.Append($" AND CreatedAt <= {name}");
            parameters.Add(new SqliteParameter(name, request.CreatedBefore.ToDateTimeOffset().ToString("o")));
        }

        if (request.UpdatedAfter is not null)
        {
            string name = $"@p{paramIdx++}";
            sb.Append($" AND UpdatedAt >= {name}");
            parameters.Add(new SqliteParameter(name, request.UpdatedAfter.ToDateTimeOffset().ToString("o")));
        }

        if (request.UpdatedBefore is not null)
        {
            string name = $"@p{paramIdx++}";
            sb.Append($" AND UpdatedAt <= {name}");
            parameters.Add(new SqliteParameter(name, request.UpdatedBefore.ToDateTimeOffset().ToString("o")));
        }

        // FTS5 subquery
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            string name = $"@q{paramIdx++}";
            sb.Append($" AND Key IN (SELECT Key FROM jira_issues ji2 WHERE ji2.Id IN (SELECT rowid FROM jira_issues_fts WHERE jira_issues_fts MATCH {name}))");
            parameters.Add(new SqliteParameter(name, FtsQueryHelper.SanitizeFtsQuery(request.Query)));
        }

        // Sorting
        string sortBy = AllowedSortColumns.Contains(request.SortBy) ? ToColumnName(request.SortBy) : "UpdatedAt";
        string sortOrder = request.SortOrder?.Equals("asc", StringComparison.OrdinalIgnoreCase) == true ? "ASC" : "DESC";
        sb.Append($" ORDER BY {sortBy} {sortOrder}");

        // Pagination
        int limit = request.Limit > 0 ? Math.Min(request.Limit, 1000) : 50;
        sb.Append(" LIMIT @limit OFFSET @offset");
        parameters.Add(new SqliteParameter("@limit", limit));
        parameters.Add(new SqliteParameter("@offset", Math.Max(0, request.Offset)));

        return (sb.ToString(), parameters);
    }

    private static void AddInClause(
        StringBuilder sb, List<SqliteParameter> parameters,
        string column, Google.Protobuf.Collections.RepeatedField<string> values, ref int paramIdx)
    {
        if (values.Count == 0) return;

        List<string> names = new List<string>();
        foreach (string v in values)
        {
            string name = $"@p{paramIdx++}";
            names.Add(name);
            parameters.Add(new SqliteParameter(name, v));
        }

        sb.Append($" AND {column} IN ({string.Join(", ", names)})");
    }

    private static void AddNotInClause(
        StringBuilder sb, List<SqliteParameter> parameters,
        string column, Google.Protobuf.Collections.RepeatedField<string> values, ref int paramIdx)
    {
        if (values.Count == 0) return;

        List<string> names = new List<string>();
        foreach (string v in values)
        {
            string name = $"@p{paramIdx++}";
            names.Add(name);
            parameters.Add(new SqliteParameter(name, v));
        }

        sb.Append($" AND {column} NOT IN ({string.Join(", ", names)})");
    }

    private static string ToColumnName(string sortBy) =>
        sortBy.ToLowerInvariant() switch
        {
            "key" => "Key",
            "title" => "Title",
            "status" => "Status",
            "priority" => "Priority",
            "type" => "Type",
            "created_at" => "CreatedAt",
            "updated_at" => "UpdatedAt",
            "resolved_at" => "ResolvedAt",
            "work_group" => "WorkGroup",
            "specification" => "Specification",
            "project_key" => "ProjectKey",
            "assignee" => "Assignee",
            "reporter" => "Reporter",
            _ => "UpdatedAt",
        };
}
