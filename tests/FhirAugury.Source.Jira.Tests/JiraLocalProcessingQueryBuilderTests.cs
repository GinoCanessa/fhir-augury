using FhirAugury.Common.Api;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Indexing;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Jira.Tests;

public class JiraLocalProcessingQueryBuilderTests
{
    [Fact]
    public void BuildList_EmptyFilter_ReturnsDefaultLimitAndOffsetOrderedByKey()
    {
        JiraLocalProcessingListRequest request = new();
        (string sql, List<SqliteParameter> parameters) = JiraLocalProcessingQueryBuilder.BuildList(request);

        Assert.Contains("FROM jira_issues WHERE 1=1", sql);
        Assert.Contains("ORDER BY Key ASC", sql);
        Assert.Contains("LIMIT @limit OFFSET @offset", sql);
        Assert.Equal(JiraLocalProcessingQueryBuilder.DefaultLimit,
            parameters.Single(p => p.ParameterName == "@limit").Value);
        Assert.Equal(0, parameters.Single(p => p.ParameterName == "@offset").Value);
    }


    [Fact]
    public void BuildList_NullListFilters_AddNoPredicates()
    {
        JiraLocalProcessingListRequest request = new JiraLocalProcessingListRequest();

        (string sql, List<SqliteParameter> _) = JiraLocalProcessingQueryBuilder.BuildList(request);

        Assert.DoesNotContain(" IN (", sql);
        Assert.DoesNotContain("LOWER(IFNULL(RelatedArtifacts,''))", sql);
        Assert.DoesNotContain("jira_issue_labels", sql);
    }

    [Fact]
    public void BuildList_EmptyListFilters_AddNoPredicates()
    {
        JiraLocalProcessingListRequest request = new JiraLocalProcessingListRequest
        {
            Projects = [],
            Specifications = [],
            Types = [],
            Priorities = [],
            Statuses = [],
            ChangeCategories = [],
            ChangeImpacts = [],
            RelatedArtifacts = [],
            WorkGroups = [],
            Reporters = [],
            Labels = [],
        };

        (string sql, List<SqliteParameter> _) = JiraLocalProcessingQueryBuilder.BuildList(request);

        Assert.DoesNotContain(" IN (", sql);
        Assert.DoesNotContain("LOWER(IFNULL(RelatedArtifacts,''))", sql);
        Assert.DoesNotContain("jira_issue_labels", sql);
    }

    [Fact]
    public void BuildRandom_NullListFilters_AddNoPredicates()
    {
        JiraLocalProcessingFilter filter = new JiraLocalProcessingFilter();

        (string sql, List<SqliteParameter> _) = JiraLocalProcessingQueryBuilder.BuildRandom(filter);

        Assert.DoesNotContain(" IN (", sql);
        Assert.DoesNotContain("LOWER(IFNULL(RelatedArtifacts,''))", sql);
        Assert.DoesNotContain("jira_issue_labels", sql);
    }

    [Fact]
    public void BuildList_PagingNormalization_CoercesInvalidValues()
    {
        (string _, List<SqliteParameter> pZero) =
            JiraLocalProcessingQueryBuilder.BuildList(new JiraLocalProcessingListRequest { Limit = 0, Offset = -5 });
        Assert.Equal(JiraLocalProcessingQueryBuilder.DefaultLimit,
            pZero.Single(p => p.ParameterName == "@limit").Value);
        Assert.Equal(0, pZero.Single(p => p.ParameterName == "@offset").Value);

        (string _, List<SqliteParameter> pCustom) =
            JiraLocalProcessingQueryBuilder.BuildList(new JiraLocalProcessingListRequest { Limit = 42, Offset = 10 });
        Assert.Equal(42, pCustom.Single(p => p.ParameterName == "@limit").Value);
        Assert.Equal(10, pCustom.Single(p => p.ParameterName == "@offset").Value);
    }

    [Fact]
    public void BuildList_SingleFacet_AddsInClause()
    {
        JiraLocalProcessingListRequest request = new() { Projects = ["FHIR", "XYZ"] };
        (string sql, List<SqliteParameter> parameters) = JiraLocalProcessingQueryBuilder.BuildList(request);

        Assert.Contains("AND ProjectKey IN (", sql);
        Assert.Equal(2, parameters.Count(p => p.Value is string s && (s == "FHIR" || s == "XYZ")));
    }

    [Fact]
    public void BuildList_MultipleFacets_AreAndJoined()
    {
        JiraLocalProcessingListRequest request = new()
        {
            Projects = ["FHIR"],
            Types = ["Bug"],
            Priorities = ["Critical"],
            Statuses = ["Open"],
            WorkGroups = ["FHIR-I"],
            Reporters = ["alice"],
            ChangeCategories = ["Substantive"],
            ChangeImpacts = ["Breaking"],
            Specifications = ["Core"],
        };
        (string sql, List<SqliteParameter> _) = JiraLocalProcessingQueryBuilder.BuildList(request);

        Assert.Contains("ProjectKey IN", sql);
        Assert.Contains("Type IN", sql);
        Assert.Contains("Priority IN", sql);
        Assert.Contains("Status IN", sql);
        Assert.Contains("WorkGroup IN", sql);
        Assert.Contains("Reporter IN", sql);
        Assert.Contains("ChangeCategory IN", sql);
        Assert.Contains("ChangeImpact IN", sql);
        Assert.Contains("Specification IN", sql);
    }

    [Fact]
    public void BuildList_RelatedArtifacts_EmitsOrOfLowerLikes()
    {
        JiraLocalProcessingListRequest request = new()
        {
            RelatedArtifacts = ["Core", "SDC"],
        };
        (string sql, List<SqliteParameter> parameters) = JiraLocalProcessingQueryBuilder.BuildList(request);

        Assert.Contains("LOWER(IFNULL(RelatedArtifacts,''))", sql);
        Assert.Contains(" OR ", sql);
        Assert.Contains("core", parameters.Select(p => p.Value?.ToString()));
        Assert.Contains("sdc", parameters.Select(p => p.Value?.ToString()));
    }

    [Fact]
    public void BuildList_Labels_SingleExistsClauseWithIn()
    {
        JiraLocalProcessingListRequest request = new() { Labels = ["l1", "l2"] };
        (string sql, List<SqliteParameter> _) = JiraLocalProcessingQueryBuilder.BuildList(request);

        int existsCount = CountOccurrences(sql, "EXISTS (SELECT 1 FROM jira_issue_labels");
        Assert.Equal(1, existsCount);
        Assert.Contains("jlab.Name IN (", sql);
    }

    [Fact]
    public void BuildList_ProcessedLocally_TrueEmitsIsNotNull()
    {
        JiraLocalProcessingListRequest request = new() { ProcessedLocally = true };
        (string sql, List<SqliteParameter> _) = JiraLocalProcessingQueryBuilder.BuildList(request);
        Assert.Contains("ProcessedLocallyAt IS NOT NULL", sql);
    }

    [Fact]
    public void BuildList_ProcessedLocally_FalseEmitsIsNull()
    {
        JiraLocalProcessingListRequest request = new() { ProcessedLocally = false };
        (string sql, List<SqliteParameter> _) = JiraLocalProcessingQueryBuilder.BuildList(request);
        Assert.Contains("ProcessedLocallyAt IS NULL", sql);
        Assert.DoesNotContain("IS NOT NULL", sql);
    }

    [Fact]
    public void BuildList_ProcessedLocally_NullEmitsNoPredicate()
    {
        JiraLocalProcessingListRequest request = new() { ProcessedLocally = null };
        (string sql, List<SqliteParameter> _) = JiraLocalProcessingQueryBuilder.BuildList(request);
        Assert.DoesNotContain("ProcessedLocallyAt", sql);
    }

    [Fact]
    public void BuildRandom_UsesOrderByRandomLimitOneAndNoPaging()
    {
        JiraLocalProcessingFilter filter = new();
        (string sql, List<SqliteParameter> parameters) = JiraLocalProcessingQueryBuilder.BuildRandom(filter);
        Assert.EndsWith("ORDER BY RANDOM() LIMIT 1", sql);
        Assert.DoesNotContain(parameters, p => p.ParameterName == "@limit");
        Assert.DoesNotContain(parameters, p => p.ParameterName == "@offset");
    }

    [Fact]
    public void BuildCount_UsesCountStar()
    {
        JiraLocalProcessingFilter filter = new() { Projects = ["FHIR"] };
        (string sql, List<SqliteParameter> parameters) = JiraLocalProcessingQueryBuilder.BuildCount(filter);
        Assert.StartsWith("SELECT COUNT(*) FROM jira_issues", sql);
        Assert.Contains("ProjectKey IN", sql);
        Assert.Contains(parameters, p => Equals(p.Value, "FHIR"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
