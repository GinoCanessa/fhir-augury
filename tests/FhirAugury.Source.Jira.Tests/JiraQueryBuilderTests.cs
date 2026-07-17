using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Indexing;

namespace FhirAugury.Source.Jira.Tests;

public class JiraQueryBuilderTests
{
    [Fact]
    public void Build_EmptyRequest_ReturnsDefaultQuery()
    {
        JiraQueryRequest request = new JiraQueryRequest();

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        Assert.Contains("SELECT * FROM jira_issues WHERE 1=1", sql);
        Assert.Contains("ORDER BY UpdatedAt DESC", sql);
        Assert.Contains("LIMIT @limit OFFSET @offset", sql);
        Assert.Equal(50, parameters.Single(p => p.ParameterName == "@limit").Value);
        Assert.Equal(0, parameters.Single(p => p.ParameterName == "@offset").Value);
    }


    [Fact]
    public void Build_NullListFilters_AddNoPredicates()
    {
        JiraQueryRequest request = new JiraQueryRequest();

        (string sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.DoesNotContain(" IN (", sql);
        Assert.DoesNotContain(" NOT IN (", sql);
        Assert.DoesNotContain("jira_issue_labels", sql);
        Assert.DoesNotContain("jira_issue_inpersons", sql);
    }

    [Fact]
    public void Build_EmptyListFilters_AddNoPredicates()
    {
        JiraQueryRequest request = new JiraQueryRequest
        {
            Statuses = [],
            Resolutions = [],
            WorkGroups = [],
            Specifications = [],
            Projects = [],
            ExcludeProjects = [],
            Types = [],
            Priorities = [],
            Labels = [],
            Assignees = [],
            Reporters = [],
            InPersonRequesters = [],
        };

        (string sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.DoesNotContain(" IN (", sql);
        Assert.DoesNotContain(" NOT IN (", sql);
        Assert.DoesNotContain("jira_issue_labels", sql);
        Assert.DoesNotContain("jira_issue_inpersons", sql);
    }

    [Fact]
    public void Build_ExcludeProjects_NullAndEmpty_AddNoNotInClause()
    {
        (string nullSql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(new JiraQueryRequest());
        (string emptySql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(new JiraQueryRequest { ExcludeProjects = [] });

        Assert.DoesNotContain("ProjectKey NOT IN", nullSql);
        Assert.DoesNotContain("ProjectKey NOT IN", emptySql);
    }

    [Fact]
    public void Build_StatusFilter_AddsInClause()
    {
        JiraQueryRequest request = new JiraQueryRequest { Statuses = ["Open", "In Progress"] };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.Contains("AND Status IN (", sql);
    }

    [Fact]
    public void Build_MultipleFilters_CombinesWithAnd()
    {
        JiraQueryRequest request = new JiraQueryRequest { Statuses = ["Open"], WorkGroups = ["FHIR-I"], Priorities = ["Critical"] };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.Contains("AND Status IN (", sql);
        Assert.Contains("AND WorkGroup IN (", sql);
        Assert.Contains("AND Priority IN (", sql);
    }

    [Fact]
    public void Build_ExcludeProjects_AddsNotInClause()
    {
        JiraQueryRequest request = new JiraQueryRequest { ExcludeProjects = ["FHIR-TEST"] };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.Contains("AND ProjectKey NOT IN (", sql);
    }

    [Fact]
    public void Build_Labels_UsesJoinTableFiltering()
    {
        JiraQueryRequest request = new JiraQueryRequest { Labels = ["bug"] };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        // Should use EXISTS subquery against join tables, not LIKE
        Assert.Contains("jira_issue_labels", sql);
        Assert.Contains("jira_index_labels", sql);
        Assert.DoesNotContain("LIKE", sql);
        // Parameter should be exact value, not wildcard
        Assert.Contains(parameters, p => p.Value!.ToString() == "bug");
        Assert.DoesNotContain(parameters, p => p.Value!.ToString() == "%bug%");
    }

    [Fact]
    public void Build_MultipleLabels_GeneratesExistsPerLabel()
    {
        JiraQueryRequest request = new JiraQueryRequest { Labels = ["bug", "urgent"] };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        // Should have two EXISTS subqueries (one per label)
        int existsCount = sql!.Split("EXISTS").Length - 1;
        Assert.Equal(2, existsCount);
        // Both labels as exact parameters
        Assert.Contains(parameters, p => p.Value!.ToString() == "bug");
        Assert.Contains(parameters, p => p.Value!.ToString() == "urgent");
    }

    [Fact]
    public void Build_LabelsAndOtherFilters_CombinesCorrectly()
    {
        JiraQueryRequest request = new JiraQueryRequest { Labels = ["bug"], Statuses = ["Open"], WorkGroups = ["TeamA"] };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter>? _) = JiraQueryBuilder.Build(request);

        Assert.Contains("jira_issue_labels", sql);
        Assert.Contains("Status IN", sql);
        Assert.Contains("WorkGroup IN", sql);
    }

    [Fact]
    public void Build_FtsQuery_AddsFtsSubquery()
    {
        JiraQueryRequest request = new JiraQueryRequest() { Query = "patient resource" };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.Contains("jira_issues_fts MATCH", sql);
    }

    [Fact]
    public void Build_AllowedSortColumn_UsesThatColumn()
    {
        JiraQueryRequest request = new JiraQueryRequest { SortBy = "created_at" };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.Contains("ORDER BY CreatedAt", sql);
    }

    [Fact]
    public void Build_DisallowedSortColumn_DefaultsToUpdatedAt()
    {
        JiraQueryRequest request = new JiraQueryRequest { SortBy = "DROP TABLE" };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.Contains("ORDER BY UpdatedAt", sql);
    }

    [Fact]
    public void Build_AscSortOrder_UsesAsc()
    {
        JiraQueryRequest request = new JiraQueryRequest { SortOrder = "asc" };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter> _) = JiraQueryBuilder.Build(request);

        Assert.Contains("ASC", sql);
    }

    [Fact]
    public void Build_LimitOverMax_CappedAt1000()
    {
        JiraQueryRequest request = new JiraQueryRequest { Limit = 5000 };

        (string _, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        Assert.Equal(1000, parameters.Single(p => p.ParameterName == "@limit").Value);
    }

    [Fact]
    public void Build_CustomPagination_SetsLimitAndOffset()
    {
        JiraQueryRequest request = new JiraQueryRequest { Limit = 25, Offset = 50 };

        (string _, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        Assert.Equal(25, parameters.Single(p => p.ParameterName == "@limit").Value);
        Assert.Equal(50, parameters.Single(p => p.ParameterName == "@offset").Value);
    }

    [Fact]
    public void Build_NegativeOffset_ClampedToZero()
    {
        JiraQueryRequest request = new JiraQueryRequest { Offset = -10 };

        (string _, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        Assert.Equal(0, parameters.Single(p => p.ParameterName == "@offset").Value);
    }

    [Fact]
    public void Build_InPersonRequesters_GeneratesExistsSubquery()
    {
        JiraQueryRequest request = new JiraQueryRequest { InPersonRequesters = ["Alice"] };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        Assert.Contains("jira_issue_inpersons", sql);
        Assert.Contains("jira_users", sql);
        Assert.Contains("EXISTS", sql);
        Assert.Contains(parameters, p => p.Value!.ToString() == "Alice");
    }

    [Fact]
    public void Build_MultipleInPersonRequesters_GeneratesMultipleExists()
    {
        JiraQueryRequest request = new JiraQueryRequest { InPersonRequesters = ["Alice", "Bob"] };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        // Should have two EXISTS subqueries (one per in-person requester)
        int existsCount = sql!.Split("jira_issue_inpersons").Length - 1;
        Assert.Equal(2, existsCount);
        Assert.Contains(parameters, p => p.Value!.ToString() == "Alice");
        Assert.Contains(parameters, p => p.Value!.ToString() == "Bob");
    }

    [Fact]
    public void Build_EmptyInPersonRequesters_NoFilter()
    {
        JiraQueryRequest request = new JiraQueryRequest();

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter>? _) = JiraQueryBuilder.Build(request);

        Assert.DoesNotContain("jira_issue_inpersons", sql);
    }

    [Fact]
    public void Build_InPersonRequesters_ParameterizedValues()
    {
        JiraQueryRequest request = new JiraQueryRequest { InPersonRequesters = ["Alice Smith"] };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        // Should not contain the display name inline
        Assert.DoesNotContain("'Alice Smith'", sql);
        Assert.Contains(parameters, p => p.Value!.ToString() == "Alice Smith");
    }

    [Fact]
    public void Build_AllParametersAreParameterized()
    {
        JiraQueryRequest request = new JiraQueryRequest { Query = "test", Statuses = ["Open"], WorkGroups = ["FHIR-I"], Labels = ["bug"] };

        (string? sql, List<Microsoft.Data.Sqlite.SqliteParameter>? parameters) = JiraQueryBuilder.Build(request);

        // All values should be via parameters, not inline
        Assert.DoesNotContain("'Open'", sql);
        Assert.DoesNotContain("'FHIR-I'", sql);
        Assert.True(parameters.Count > 0);
    }
}
