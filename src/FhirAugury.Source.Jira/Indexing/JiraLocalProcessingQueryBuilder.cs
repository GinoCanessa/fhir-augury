using System.Text;
using FhirAugury.Source.Jira.Api;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Jira.Indexing;

/// <summary>
/// Builds parameterized SQL for the FR-02 local-processing endpoints.
/// OR semantics within a field, AND semantics across fields.
/// </summary>
public static class JiraLocalProcessingQueryBuilder
{
    public const int DefaultLimit = 500;

    private const string SelectColumns =
        "Key, ProjectKey, Title, Type, Status, Priority, WorkGroup, Specification, UpdatedAt";

    /// <summary>
    /// Builds the WHERE clause plus parameters for the supplied filter.
    /// Returned SQL starts with <c>" WHERE 1=1 ..."</c>.
    /// </summary>
    public static (string WhereSql, List<SqliteParameter> Parameters)
        BuildWhere(JiraLocalProcessingFilter filter)
    {
        StringBuilder sb = new StringBuilder(" WHERE 1=1");
        List<SqliteParameter> parameters = [];
        int paramIdx = 0;

        AddInClause(sb, parameters, "ProjectKey", filter.Projects, ref paramIdx);
        AddInClause(sb, parameters, "Specification", filter.Specifications, ref paramIdx);
        AddInClause(sb, parameters, "Type", filter.Types, ref paramIdx);
        AddInClause(sb, parameters, "Priority", filter.Priorities, ref paramIdx);
        AddInClause(sb, parameters, "Status", filter.Statuses, ref paramIdx);
        AddInClause(sb, parameters, "ChangeCategory", filter.ChangeCategories, ref paramIdx);
        AddInClause(sb, parameters, "ChangeImpact", filter.ChangeImpacts, ref paramIdx);
        AddInClause(sb, parameters, "WorkGroup", filter.WorkGroups, ref paramIdx);
        AddInClause(sb, parameters, "Reporter", filter.Reporters, ref paramIdx);

        AddRelatedArtifactsClause(sb, parameters, filter.RelatedArtifacts, ref paramIdx);
        AddLabelsClause(sb, parameters, filter.Labels, ref paramIdx);

        ProcessedLocallyMapper.AppendFilter(sb, parameters, filter.ProcessedLocally, ref paramIdx);

        return (sb.ToString(), parameters);
    }

    /// <summary>Builds the paged list query.</summary>
    public static (string Sql, List<SqliteParameter> Parameters)
        BuildList(JiraLocalProcessingListRequest request)
    {
        (string where, List<SqliteParameter> parameters) = BuildWhere(request);

        int limit = (request.Limit is null || request.Limit.Value <= 0)
            ? DefaultLimit
            : request.Limit.Value;
        int offset = (request.Offset is null || request.Offset.Value < 0)
            ? 0
            : request.Offset.Value;

        StringBuilder sb = new StringBuilder();
        sb.Append($"SELECT {SelectColumns} FROM jira_issues");
        sb.Append(where);
        sb.Append(" ORDER BY Key ASC");
        sb.Append(" LIMIT @limit OFFSET @offset");

        parameters.Add(new SqliteParameter("@limit", limit));
        parameters.Add(new SqliteParameter("@offset", offset));

        return (sb.ToString(), parameters);
    }

    /// <summary>Builds the random-single-ticket query.</summary>
    public static (string Sql, List<SqliteParameter> Parameters)
        BuildRandom(JiraLocalProcessingFilter filter)
    {
        (string where, List<SqliteParameter> parameters) = BuildWhere(filter);

        string sql = $"SELECT {SelectColumns} FROM jira_issues{where} ORDER BY RANDOM() LIMIT 1";
        return (sql, parameters);
    }

    /// <summary>Builds the count query for paging totals.</summary>
    public static (string Sql, List<SqliteParameter> Parameters)
        BuildCount(JiraLocalProcessingFilter filter)
    {
        (string where, List<SqliteParameter> parameters) = BuildWhere(filter);

        string sql = $"SELECT COUNT(*) FROM jira_issues{where}";
        return (sql, parameters);
    }

    private static void AddInClause(
        StringBuilder sb, List<SqliteParameter> parameters,
        string column, List<string> values, ref int paramIdx)
    {
        if (values.Count == 0) return;

        List<string> names = [];
        foreach (string v in values)
        {
            string name = $"@p{paramIdx++}";
            names.Add(name);
            parameters.Add(new SqliteParameter(name, v));
        }

        sb.Append($" AND {column} IN ({string.Join(", ", names)})");
    }

    private static void AddRelatedArtifactsClause(
        StringBuilder sb, List<SqliteParameter> parameters,
        List<string> values, ref int paramIdx)
    {
        if (values.Count == 0) return;

        List<string> predicates = [];
        foreach (string v in values)
        {
            string name = $"@p{paramIdx++}";
            predicates.Add($"LOWER(IFNULL(RelatedArtifacts,'')) LIKE '%' || {name} || '%'");
            parameters.Add(new SqliteParameter(name, v.ToLowerInvariant()));
        }

        sb.Append(" AND (");
        sb.Append(string.Join(" OR ", predicates));
        sb.Append(')');
    }

    private static void AddLabelsClause(
        StringBuilder sb, List<SqliteParameter> parameters,
        List<string> values, ref int paramIdx)
    {
        if (values.Count == 0) return;

        List<string> names = [];
        foreach (string v in values)
        {
            string name = $"@p{paramIdx++}";
            names.Add(name);
            parameters.Add(new SqliteParameter(name, v));
        }

        sb.Append(" AND EXISTS (SELECT 1 FROM jira_issue_labels jil");
        sb.Append(" INNER JOIN jira_index_labels jlab ON jil.LabelId = jlab.Id");
        sb.Append(" WHERE jil.IssueId = jira_issues.Id");
        sb.Append($" AND jlab.Name IN ({string.Join(", ", names)}))");
    }
}
