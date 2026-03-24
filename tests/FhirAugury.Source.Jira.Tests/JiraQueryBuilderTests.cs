using Fhiraugury;
using FhirAugury.Source.Jira.Indexing;

namespace FhirAugury.Source.Jira.Tests;

public class JiraQueryBuilderTests
{
    [Fact]
    public void Build_EmptyRequest_ReturnsDefaultQuery()
    {
        JiraQueryRequest request = new Fhiraugury.JiraQueryRequest();

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        Assert.Contains("SELECT * FROM jira_issues WHERE 1=1", sql);
        Assert.Contains("ORDER BY UpdatedAt DESC", sql);
        Assert.Contains("LIMIT @limit OFFSET @offset", sql);
        Assert.Equal(50, parameters.Single(p => p.ParameterName == "@limit").Value);
        Assert.Equal(0, parameters.Single(p => p.ParameterName == "@offset").Value);
    }

    [Fact]
    public void Build_StatusFilter_AddsInClause()
    {
        JiraQueryRequest request = new Fhiraugury.JiraQueryRequest();
        request.Statuses.Add("Open");
        request.Statuses.Add("In Progress");

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.Contains("AND Status IN (", sql);
    }

    [Fact]
    public void Build_MultipleFilters_CombinesWithAnd()
    {
        JiraQueryRequest request = new Fhiraugury.JiraQueryRequest();
        request.Statuses.Add("Open");
        request.WorkGroups.Add("FHIR-I");
        request.Priorities.Add("Critical");

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.Contains("AND Status IN (", sql);
        Assert.Contains("AND WorkGroup IN (", sql);
        Assert.Contains("AND Priority IN (", sql);
    }

    [Fact]
    public void Build_ExcludeProjects_AddsNotInClause()
    {
        JiraQueryRequest request = new Fhiraugury.JiraQueryRequest();
        request.ExcludeProjects.Add("FHIR-TEST");

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.Contains("AND ProjectKey NOT IN (", sql);
    }

    [Fact]
    public void Build_Labels_UsesLikeMatching()
    {
        JiraQueryRequest request = new Fhiraugury.JiraQueryRequest();
        request.Labels.Add("bug");

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        Assert.Contains("AND Labels LIKE", sql);
        Assert.Contains(parameters, p => p.Value!.ToString() == "%bug%");
    }

    [Fact]
    public void Build_FtsQuery_AddsFtsSubquery()
    {
        JiraQueryRequest request = new Fhiraugury.JiraQueryRequest();
        request.Query = "patient resource";

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.Contains("jira_issues_fts MATCH", sql);
    }

    [Fact]
    public void Build_AllowedSortColumn_UsesThatColumn()
    {
        JiraQueryRequest request = new Fhiraugury.JiraQueryRequest { SortBy = "created_at" };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.Contains("ORDER BY CreatedAt", sql);
    }

    [Fact]
    public void Build_DisallowedSortColumn_DefaultsToUpdatedAt()
    {
        JiraQueryRequest request = new Fhiraugury.JiraQueryRequest { SortBy = "DROP TABLE" };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.Contains("ORDER BY UpdatedAt", sql);
    }

    [Fact]
    public void Build_AscSortOrder_UsesAsc()
    {
        JiraQueryRequest request = new Fhiraugury.JiraQueryRequest { SortOrder = "asc" };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.Contains("ASC", sql);
    }

    [Fact]
    public void Build_LimitOverMax_CappedAt1000()
    {
        JiraQueryRequest request = new Fhiraugury.JiraQueryRequest { Limit = 5000 };

        (string _, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        Assert.Equal(1000, parameters.Single(p => p.ParameterName == "@limit").Value);
    }

    [Fact]
    public void Build_CustomPagination_SetsLimitAndOffset()
    {
        JiraQueryRequest request = new Fhiraugury.JiraQueryRequest { Limit = 25, Offset = 50 };

        (string _, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        Assert.Equal(25, parameters.Single(p => p.ParameterName == "@limit").Value);
        Assert.Equal(50, parameters.Single(p => p.ParameterName == "@offset").Value);
    }

    [Fact]
    public void Build_NegativeOffset_ClampedToZero()
    {
        JiraQueryRequest request = new Fhiraugury.JiraQueryRequest { Offset = -10 };

        (string _, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        Assert.Equal(0, parameters.Single(p => p.ParameterName == "@offset").Value);
    }

    [Fact]
    public void Build_AllParametersAreParameterized()
    {
        JiraQueryRequest request = new Fhiraugury.JiraQueryRequest();
        request.Statuses.Add("Open");
        request.WorkGroups.Add("FHIR-I");
        request.Query = "test";
        request.Labels.Add("bug");

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        // All values should be via parameters, not inline
        Assert.DoesNotContain("'Open'", sql);
        Assert.DoesNotContain("'FHIR-I'", sql);
        Assert.True(parameters.Count > 0);
    }
}
