using System.Text;
using FhirAugury.Source.Jira.Api;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Jira.Indexing;

/// <summary>
/// Builds parameterized SQL for the FR-02 local-processing endpoints.
/// OR semantics within a field, AND semantics across fields.
/// </summary>
/// <remarks>
/// Parameterised by <see cref="TableMapping"/> so the same filter shape
/// can be applied to any of the four Jira-issue-shaped tables
/// (<c>jira_issues</c>, <c>jira_pss</c>, <c>jira_baldef</c>,
/// <c>jira_ballot</c>). Tables that lack a given column (e.g. PSS has no
/// Specification) map it to NULL in the projection and silently drop the
/// corresponding IN filter rather than raising. The default overloads
/// preserve the pre-split FHIR-change-request behaviour.
/// </remarks>
public static class JiraLocalProcessingQueryBuilder
{
    public const int DefaultLimit = 500;

    /// <summary>Per-shape table mapping: table name + column expressions for
    /// filterable fields that vary across shapes.</summary>
    public sealed record TableMapping(
        string Type,
        string TableName,
        string? WorkGroupExpr,
        string? SpecificationExpr,
        bool HasRelatedArtifacts,
        bool HasChangeCategory,
        bool HasChangeImpact);

    public static readonly TableMapping Fhir = new TableMapping(
        "fhir", "jira_issues", "WorkGroup", "Specification",
        HasRelatedArtifacts: true, HasChangeCategory: true, HasChangeImpact: true);

    public static readonly TableMapping Pss = new TableMapping(
        "pss", "jira_pss",
        WorkGroupExpr: "COALESCE(SponsoringWorkGroup, SponsoringWorkGroupsLegacy)",
        SpecificationExpr: null,
        HasRelatedArtifacts: false, HasChangeCategory: false, HasChangeImpact: false);

    public static readonly TableMapping Baldef = new TableMapping(
        "baldef", "jira_baldef",
        WorkGroupExpr: null,
        SpecificationExpr: "Specification",
        HasRelatedArtifacts: true, HasChangeCategory: false, HasChangeImpact: false);

    public static readonly TableMapping Ballot = new TableMapping(
        "ballot", "jira_ballot",
        WorkGroupExpr: null,
        SpecificationExpr: "Specification",
        HasRelatedArtifacts: false, HasChangeCategory: false, HasChangeImpact: false);

    private static readonly Dictionary<string, TableMapping> MappingByType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fhir"] = Fhir,
        ["pss"] = Pss,
        ["baldef"] = Baldef,
        ["ballot"] = Ballot,
    };

    public static IReadOnlyCollection<TableMapping> AllMappings => MappingByType.Values;

    /// <summary>Resolve a type qualifier (e.g. "fhir", "pss") to its mapping.
    /// Returns <c>null</c> if unknown.</summary>
    public static TableMapping? TryGetMapping(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return Fhir;
        return MappingByType.TryGetValue(type, out TableMapping? m) ? m : null;
    }

    /// <summary>
    /// Builds the WHERE clause plus parameters for the supplied filter.
    /// Returned SQL starts with <c>" WHERE 1=1 ..."</c>.
    /// </summary>
    public static (string WhereSql, List<SqliteParameter> Parameters)
        BuildWhere(JiraLocalProcessingFilter filter) => BuildWhere(filter, Fhir);

    public static (string WhereSql, List<SqliteParameter> Parameters)
        BuildWhere(JiraLocalProcessingFilter filter, TableMapping mapping)
    {
        StringBuilder sb = new StringBuilder(" WHERE 1=1");
        List<SqliteParameter> parameters = [];
        int paramIdx = 0;

        AddInClause(sb, parameters, "ProjectKey", filter.Projects, ref paramIdx);
        if (mapping.SpecificationExpr is not null)
            AddInClause(sb, parameters, mapping.SpecificationExpr, filter.Specifications, ref paramIdx);
        AddInClause(sb, parameters, "Type", filter.Types, ref paramIdx);
        AddInClause(sb, parameters, "Priority", filter.Priorities, ref paramIdx);
        AddInClause(sb, parameters, "Status", filter.Statuses, ref paramIdx);
        if (mapping.HasChangeCategory)
            AddInClause(sb, parameters, "ChangeCategory", filter.ChangeCategories, ref paramIdx);
        if (mapping.HasChangeImpact)
            AddInClause(sb, parameters, "ChangeImpact", filter.ChangeImpacts, ref paramIdx);
        if (mapping.WorkGroupExpr is not null)
            AddInClause(sb, parameters, mapping.WorkGroupExpr, filter.WorkGroups, ref paramIdx);
        AddInClause(sb, parameters, "Reporter", filter.Reporters, ref paramIdx);

        if (mapping.HasRelatedArtifacts)
            AddRelatedArtifactsClause(sb, parameters, filter.RelatedArtifacts, ref paramIdx);
        AddLabelsClause(sb, parameters, filter.Labels, mapping.TableName, ref paramIdx);

        ProcessedLocallyMapper.AppendFilter(sb, parameters, filter.ProcessedLocally, ref paramIdx);

        return (sb.ToString(), parameters);
    }

    /// <summary>Builds the paged list query.</summary>
    public static (string Sql, List<SqliteParameter> Parameters)
        BuildList(JiraLocalProcessingListRequest request) => BuildList(request, Fhir);

    public static (string Sql, List<SqliteParameter> Parameters)
        BuildList(JiraLocalProcessingListRequest request, TableMapping mapping)
    {
        (string where, List<SqliteParameter> parameters) = BuildWhere(request, mapping);

        int limit = (request.Limit is null || request.Limit.Value <= 0)
            ? DefaultLimit
            : request.Limit.Value;
        int offset = (request.Offset is null || request.Offset.Value < 0)
            ? 0
            : request.Offset.Value;

        StringBuilder sb = new StringBuilder();
        sb.Append($"SELECT {BuildSelectColumns(mapping)} FROM {mapping.TableName}");
        sb.Append(where);
        sb.Append(" ORDER BY Key ASC");
        sb.Append(" LIMIT @limit OFFSET @offset");

        parameters.Add(new SqliteParameter("@limit", limit));
        parameters.Add(new SqliteParameter("@offset", offset));

        return (sb.ToString(), parameters);
    }

    /// <summary>Builds the random-single-ticket query.</summary>
    public static (string Sql, List<SqliteParameter> Parameters)
        BuildRandom(JiraLocalProcessingFilter filter) => BuildRandom(filter, Fhir);

    public static (string Sql, List<SqliteParameter> Parameters)
        BuildRandom(JiraLocalProcessingFilter filter, TableMapping mapping)
    {
        (string where, List<SqliteParameter> parameters) = BuildWhere(filter, mapping);

        string sql =
            $"SELECT {BuildSelectColumns(mapping)} FROM {mapping.TableName}{where} ORDER BY RANDOM() LIMIT 1";
        return (sql, parameters);
    }

    /// <summary>Builds the count query for paging totals.</summary>
    public static (string Sql, List<SqliteParameter> Parameters)
        BuildCount(JiraLocalProcessingFilter filter) => BuildCount(filter, Fhir);

    public static (string Sql, List<SqliteParameter> Parameters)
        BuildCount(JiraLocalProcessingFilter filter, TableMapping mapping)
    {
        (string where, List<SqliteParameter> parameters) = BuildWhere(filter, mapping);

        string sql = $"SELECT COUNT(*) FROM {mapping.TableName}{where}";
        return (sql, parameters);
    }

    private static string BuildSelectColumns(TableMapping mapping)
    {
        string workGroup = mapping.WorkGroupExpr is null
            ? "NULL AS WorkGroup"
            : $"{mapping.WorkGroupExpr} AS WorkGroup";
        string specification = mapping.SpecificationExpr is null
            ? "NULL AS Specification"
            : $"{mapping.SpecificationExpr} AS Specification";
        return $"Key, ProjectKey, Title, Type, Status, Priority, {workGroup}, {specification}, UpdatedAt";
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
        List<string> values, string tableName, ref int paramIdx)
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
        sb.Append($" WHERE jil.IssueKey = {tableName}.Key");
        sb.Append($" AND jlab.Name IN ({string.Join(", ", names)}))");
    }
}
