using System.Text.Json;
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

    [Fact]
    public void BuildSelectionWhere_ComposesParameterizedGroupsAndKeys()
    {
        string[] includes = [" Inc-01 ", "Inc-01", "inc-01", "x' OR 1=1 --", @"path\label", "%", "_", " Inc-01 "];
        string[] excludes = [" ex-01 ", "ex-02", "ex-02"];
        string[] keys = ["FHIR-1", " FHIR-2 ", "FHIR-' OR 1=1 --"];
        JiraLocalProcessingSelectionRequest request = new()
        {
            Projects = ["FHIR"],
            Specifications = ["Core"],
            Types = ["Bug"],
            Priorities = ["Major"],
            Statuses = ["Open"],
            ChangeCategories = ["Substantive"],
            ChangeImpacts = ["Breaking"],
            RelatedArtifacts = ["US Core"],
            WorkGroups = ["FHIR-I"],
            Reporters = ["alice"],
            Labels = ["Exact-L1", "Exact-L2"],
            ProcessedLocally = true,
            Keys = [.. keys],
            LabelText = new JiraLabelTextFilter
            {
                Includes = ["", " \t\r\n", .. includes],
                Excludes = ["", " \t\r\n", .. excludes],
            },
            Limit = 42,
            Offset = 5,
        };

        (string inheritedWhere, List<SqliteParameter> inheritedParameters) =
            JiraLocalProcessingQueryBuilder.BuildWhere(request);
        (string where, List<SqliteParameter> parameters) =
            JiraLocalProcessingQueryBuilder.BuildSelectionWhere(request);

        Assert.StartsWith(inheritedWhere, where);
        Assert.Contains("AND ProcessedLocallyAt IS NOT NULL", inheritedWhere);
        Assert.Contains("AND jlab.Name IN (", inheritedWhere);
        Assert.Equal(
            inheritedParameters.Select(p => (p.ParameterName, p.Value)),
            parameters.Take(inheritedParameters.Count).Select(p => (p.ParameterName, p.Value)));
        string selection = where[inheritedWhere.Length..];
        Assert.Equal(
            " AND Key IN (@selectionKey0, @selectionKey1, @selectionKey2)" +
            " AND (Labels LIKE @labelTextInclude0 OR Labels LIKE @labelTextInclude1" +
            " OR Labels LIKE @labelTextInclude2 OR Labels LIKE @labelTextInclude3" +
            " OR Labels LIKE @labelTextInclude4 OR Labels LIKE @labelTextInclude5" +
            " OR Labels LIKE @labelTextInclude6 OR Labels LIKE @labelTextInclude7)" +
            " AND (Labels IS NOT NULL AND Labels NOT LIKE @labelTextExclude0" +
            " AND Labels NOT LIKE @labelTextExclude1 AND Labels NOT LIKE @labelTextExclude2)",
            selection);
        Assert.DoesNotContain("Inc-01", selection);
        Assert.DoesNotContain("ex-02", selection);
        Assert.DoesNotContain("OR 1=1", selection);
        Assert.DoesNotContain(@"path\label", selection);
        Assert.DoesNotContain("%", selection);
        Assert.DoesNotContain("_", selection);
        Assert.DoesNotContain("COALESCE", selection);
        Assert.DoesNotContain("LOWER", selection);
        Assert.DoesNotContain("ESCAPE", selection);
        Assert.Equal(keys, parameters.Where(p => p.ParameterName.StartsWith("@selectionKey")).Select(p => p.Value));
        Assert.Equal(
            includes.Select(value => $"%{value}%"),
            parameters.Where(p => p.ParameterName.StartsWith("@labelTextInclude")).Select(p => p.Value));
        Assert.Equal(
            excludes.Select(value => $"%{value}%"),
            parameters.Where(p => p.ParameterName.StartsWith("@labelTextExclude")).Select(p => p.Value));
        Assert.Equal(inheritedParameters.Count + keys.Length + includes.Length + excludes.Length, parameters.Count);

        (string _, List<SqliteParameter> listParameters) =
            JiraLocalProcessingQueryBuilder.BuildSelectionList(request);
        Assert.Equal(listParameters.Count, listParameters.Select(p => p.ParameterName).Distinct().Count());
        Assert.Equal(42, listParameters.Single(p => p.ParameterName == "@limit").Value);
        Assert.Equal(5, listParameters.Single(p => p.ParameterName == "@offset").Value);

        (string oldList, List<SqliteParameter> oldListParameters) = JiraLocalProcessingQueryBuilder.BuildList(request);
        (string oldCount, List<SqliteParameter> _) = JiraLocalProcessingQueryBuilder.BuildCount(request);
        (string oldRandom, List<SqliteParameter> _) = JiraLocalProcessingQueryBuilder.BuildRandom(request);
        Assert.Contains(inheritedWhere, oldList);
        Assert.EndsWith(inheritedWhere, oldCount);
        Assert.Contains(inheritedWhere, oldRandom);
        Assert.DoesNotContain("@labelText", oldList + oldCount + oldRandom);
        Assert.DoesNotContain("@selectionKey", oldList + oldCount + oldRandom);
        Assert.Equal(inheritedParameters.Count + 2, oldListParameters.Count);
    }

    [Theory]
    [InlineData("fhir", "jira_issues", "WorkGroup", "Specification", true, true, true)]
    [InlineData("pss", "jira_pss", "COALESCE(SponsoringWorkGroup, SponsoringWorkGroupsLegacy)", null, false, false, false)]
    [InlineData("baldef", "jira_baldef", null, "Specification", true, false, false)]
    [InlineData("ballot", "jira_ballot", null, "Specification", false, false, false)]
    public void BuildSelectionListAndCount_UseIdenticalPredicates(
        string type,
        string table,
        string? workGroupExpression,
        string? specificationExpression,
        bool hasRelatedArtifacts,
        bool hasChangeCategory,
        bool hasChangeImpact)
    {
        JiraLocalProcessingQueryBuilder.TableMapping mapping =
            Assert.IsType<JiraLocalProcessingQueryBuilder.TableMapping>(JiraLocalProcessingQueryBuilder.TryGetMapping(type));
        Assert.Equal(type, mapping.Type);
        Assert.Equal(table, mapping.TableName);
        Assert.Equal(workGroupExpression, mapping.WorkGroupExpr);
        Assert.Equal(specificationExpression, mapping.SpecificationExpr);
        Assert.Equal(hasRelatedArtifacts, mapping.HasRelatedArtifacts);
        Assert.Equal(hasChangeCategory, mapping.HasChangeCategory);
        Assert.Equal(hasChangeImpact, mapping.HasChangeImpact);

        JiraLocalProcessingSelectionRequest request = new()
        {
            Projects = ["FHIR"],
            Specifications = ["Core"],
            Types = ["Bug"],
            Priorities = ["Major"],
            Statuses = ["Open"],
            ChangeCategories = ["Substantive"],
            ChangeImpacts = ["Breaking"],
            RelatedArtifacts = ["US Core"],
            WorkGroups = ["FHIR-I"],
            Reporters = ["alice"],
            Labels = ["exact"],
            ProcessedLocally = false,
            Keys = ["FHIR-1", "FHIR-2"],
            LabelText = new JiraLabelTextFilter { Includes = ["inc-01", "inc-02"], Excludes = ["ex-01", "ex-02"] },
            Limit = 7,
            Offset = 3,
        };
        (string where, List<SqliteParameter> whereParameters) =
            JiraLocalProcessingQueryBuilder.BuildSelectionWhere(request, mapping);
        (string list, List<SqliteParameter> listParameters) =
            JiraLocalProcessingQueryBuilder.BuildSelectionList(request, mapping);
        (string count, List<SqliteParameter> countParameters) =
            JiraLocalProcessingQueryBuilder.BuildSelectionCount(request, mapping);

        string workGroup = workGroupExpression is null ? "NULL AS WorkGroup" : $"{workGroupExpression} AS WorkGroup";
        string specification = specificationExpression is null ? "NULL AS Specification" : $"{specificationExpression} AS Specification";
        Assert.Equal(
            $"SELECT Key, ProjectKey, Title, Type, Status, Priority, {workGroup}, {specification}, UpdatedAt FROM {table}" +
            $"{where} ORDER BY Key ASC LIMIT @limit OFFSET @offset",
            list);
        Assert.Equal($"SELECT COUNT(*) FROM {table}{where}", count);
        Assert.Equal(
            whereParameters.Select(p => (p.ParameterName, p.Value)),
            countParameters.Select(p => (p.ParameterName, p.Value)));
        Assert.Equal(
            whereParameters.Select(p => (p.ParameterName, p.Value)),
            listParameters.Take(whereParameters.Count).Select(p => (p.ParameterName, p.Value)));
        Assert.Equal(whereParameters.Count + 2, listParameters.Count);
        Assert.Equal(7, listParameters.Single(p => p.ParameterName == "@limit").Value);
        Assert.Equal(3, listParameters.Single(p => p.ParameterName == "@offset").Value);
        Assert.Contains($"WHERE jil.IssueKey = {table}.Key", where);
        Assert.Contains("AND ProcessedLocallyAt IS NULL", where);
        Assert.Equal(workGroupExpression is not null, whereParameters.Any(p => Equals(p.Value, "FHIR-I")));
        Assert.Equal(specificationExpression is not null, whereParameters.Any(p => Equals(p.Value, "Core")));
        if (workGroupExpression is not null)
        {
            Assert.Contains($"AND {workGroupExpression} IN (", where);
        }
        if (specificationExpression is not null)
        {
            Assert.Contains($"AND {specificationExpression} IN (", where);
        }
        Assert.Equal(hasRelatedArtifacts, where.Contains("LOWER(IFNULL(RelatedArtifacts,''))"));
        Assert.Equal(hasChangeCategory, where.Contains("ChangeCategory IN ("));
        Assert.Equal(hasChangeImpact, where.Contains("ChangeImpact IN ("));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildSelectionWhere_EmptyKeysAddNoRestriction(bool empty)
    {
        JiraLocalProcessingSelectionRequest request = new()
        {
            Projects = ["FHIR"],
            Keys = empty ? [] : null,
            LabelText = new JiraLabelTextFilter { Includes = ["inc-01"] },
        };

        (string where, List<SqliteParameter> parameters) = JiraLocalProcessingQueryBuilder.BuildSelectionWhere(request);

        Assert.Equal(" WHERE 1=1 AND ProjectKey IN (@p0) AND (Labels LIKE @labelTextInclude0)", where);
        Assert.DoesNotContain(" AND Key IN (", where);
        Assert.DoesNotContain(parameters, p => p.ParameterName.StartsWith("@selectionKey"));
        Assert.Equal(2, parameters.Count);
    }

    [Theory]
    [InlineData(null, null, 500, 0)]
    [InlineData(0, -5, 500, 0)]
    [InlineData(-1, 4, 500, 4)]
    [InlineData(42, 10, 42, 10)]
    public void BuildSelectionList_NormalizesPaging(int? limit, int? offset, int expectedLimit, int expectedOffset)
    {
        JiraLocalProcessingSelectionRequest request = new() { Limit = limit, Offset = offset };
        (string sql, List<SqliteParameter> parameters) = JiraLocalProcessingQueryBuilder.BuildSelectionList(request);
        (string oldSql, List<SqliteParameter> oldParameters) = JiraLocalProcessingQueryBuilder.BuildList(request);

        Assert.Equal(oldSql, sql);
        Assert.Equal(oldParameters.Select(p => (p.ParameterName, p.Value)), parameters.Select(p => (p.ParameterName, p.Value)));
        Assert.EndsWith("ORDER BY Key ASC LIMIT @limit OFFSET @offset", sql);
        Assert.Equal(expectedLimit, parameters.Single(p => p.ParameterName == "@limit").Value);
        Assert.Equal(expectedOffset, parameters.Single(p => p.ParameterName == "@offset").Value);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"includes":null,"excludes":null}""")]
    [InlineData("""{"includes":[],"excludes":[]}""")]
    [InlineData("""{"includes":[null,""," \t\r\n"],"excludes":[null,""," \t\r\n"]}""")]
    public void BuildSelectionWhere_InactiveLabelTextAddsNoRestriction(string labelTextJson)
    {
        JiraLocalProcessingSelectionRequest request = new()
        {
            Projects = ["FHIR"],
            ProcessedLocally = false,
            LabelText = JsonSerializer.Deserialize<JiraLabelTextFilter>(labelTextJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        (string where, List<SqliteParameter> parameters) = JiraLocalProcessingQueryBuilder.BuildSelectionWhere(request);
        (string inheritedWhere, List<SqliteParameter> inheritedParameters) = JiraLocalProcessingQueryBuilder.BuildWhere(request);

        Assert.Equal(inheritedWhere, where);
        Assert.Equal(inheritedParameters.Select(p => (p.ParameterName, p.Value)), parameters.Select(p => (p.ParameterName, p.Value)));
        Assert.DoesNotContain("Labels", where);
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
