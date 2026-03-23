using FhirAugury.Source.Jira.Indexing;

namespace FhirAugury.Source.Jira.Tests;

public class JiraQueryBuilderTests
{
    [Fact]
    public void Build_EmptyRequest_ReturnsDefaultQuery()
    {
        var request = new Fhiraugury.JiraQueryRequest();

        var (sql, parameters) = JiraQueryBuilder.Build(request);

        Assert.Contains("SELECT * FROM jira_issues WHERE 1=1", sql);
        Assert.Contains("ORDER BY UpdatedAt DESC", sql);
        Assert.Contains("LIMIT @limit OFFSET @offset", sql);
        Assert.Equal(50, parameters.Single(p => p.ParameterName == "@limit").Value);
        Assert.Equal(0, parameters.Single(p => p.ParameterName == "@offset").Value);
    }

    [Fact]
    public void Build_StatusFilter_AddsInClause()
    {
        var request = new Fhiraugury.JiraQueryRequest();
        request.Statuses.Add("Open");
        request.Statuses.Add("In Progress");

        var (sql, _) = JiraQueryBuilder.Build(request);

        Assert.Contains("AND Status IN (", sql);
    }

    [Fact]
    public void Build_MultipleFilters_CombinesWithAnd()
    {
        var request = new Fhiraugury.JiraQueryRequest();
        request.Statuses.Add("Open");
        request.WorkGroups.Add("FHIR-I");
        request.Priorities.Add("Critical");

        var (sql, _) = JiraQueryBuilder.Build(request);

        Assert.Contains("AND Status IN (", sql);
        Assert.Contains("AND WorkGroup IN (", sql);
        Assert.Contains("AND Priority IN (", sql);
    }

    [Fact]
    public void Build_ExcludeProjects_AddsNotInClause()
    {
        var request = new Fhiraugury.JiraQueryRequest();
        request.ExcludeProjects.Add("FHIR-TEST");

        var (sql, _) = JiraQueryBuilder.Build(request);

        Assert.Contains("AND ProjectKey NOT IN (", sql);
    }

    [Fact]
    public void Build_Labels_UsesLikeMatching()
    {
        var request = new Fhiraugury.JiraQueryRequest();
        request.Labels.Add("bug");

        var (sql, parameters) = JiraQueryBuilder.Build(request);

        Assert.Contains("AND Labels LIKE", sql);
        Assert.Contains(parameters, p => p.Value!.ToString() == "%bug%");
    }

    [Fact]
    public void Build_FtsQuery_AddsFtsSubquery()
    {
        var request = new Fhiraugury.JiraQueryRequest();
        request.Query = "patient resource";

        var (sql, _) = JiraQueryBuilder.Build(request);

        Assert.Contains("jira_issues_fts MATCH", sql);
    }

    [Fact]
    public void Build_AllowedSortColumn_UsesThatColumn()
    {
        var request = new Fhiraugury.JiraQueryRequest { SortBy = "created_at" };

        var (sql, _) = JiraQueryBuilder.Build(request);

        Assert.Contains("ORDER BY CreatedAt", sql);
    }

    [Fact]
    public void Build_DisallowedSortColumn_DefaultsToUpdatedAt()
    {
        var request = new Fhiraugury.JiraQueryRequest { SortBy = "DROP TABLE" };

        var (sql, _) = JiraQueryBuilder.Build(request);

        Assert.Contains("ORDER BY UpdatedAt", sql);
    }

    [Fact]
    public void Build_AscSortOrder_UsesAsc()
    {
        var request = new Fhiraugury.JiraQueryRequest { SortOrder = "asc" };

        var (sql, _) = JiraQueryBuilder.Build(request);

        Assert.Contains("ASC", sql);
    }

    [Fact]
    public void Build_LimitOverMax_CappedAt1000()
    {
        var request = new Fhiraugury.JiraQueryRequest { Limit = 5000 };

        var (_, parameters) = JiraQueryBuilder.Build(request);

        Assert.Equal(1000, parameters.Single(p => p.ParameterName == "@limit").Value);
    }

    [Fact]
    public void Build_CustomPagination_SetsLimitAndOffset()
    {
        var request = new Fhiraugury.JiraQueryRequest { Limit = 25, Offset = 50 };

        var (_, parameters) = JiraQueryBuilder.Build(request);

        Assert.Equal(25, parameters.Single(p => p.ParameterName == "@limit").Value);
        Assert.Equal(50, parameters.Single(p => p.ParameterName == "@offset").Value);
    }

    [Fact]
    public void Build_NegativeOffset_ClampedToZero()
    {
        var request = new Fhiraugury.JiraQueryRequest { Offset = -10 };

        var (_, parameters) = JiraQueryBuilder.Build(request);

        Assert.Equal(0, parameters.Single(p => p.ParameterName == "@offset").Value);
    }

    [Fact]
    public void Build_AllParametersAreParameterized()
    {
        var request = new Fhiraugury.JiraQueryRequest();
        request.Statuses.Add("Open");
        request.WorkGroups.Add("FHIR-I");
        request.Query = "test";
        request.Labels.Add("bug");

        var (sql, parameters) = JiraQueryBuilder.Build(request);

        // All values should be via parameters, not inline
        Assert.DoesNotContain("'Open'", sql);
        Assert.DoesNotContain("'FHIR-I'", sql);
        Assert.True(parameters.Count > 0);
    }
}
