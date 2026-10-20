using FhirAugury.Common.Api;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Filtering;

namespace FhirAugury.Processing.Jira.Common.Tests.Filtering;

public class JiraProcessingFilterResolverTests
{
    [Fact]
    public void Resolve_NullUsesProcessorDefault_WhenDefaultExists()
    {
        JiraProcessingFilterResolver resolver = new(new JiraProcessingFilterDefaults { TicketStatusesToProcess = ["Triaged"] });

        ResolvedJiraProcessingFilters filters = resolver.Resolve(new JiraProcessingOptions { AgentCliCommand = "agent {ticketKey}", JiraSourceAddress = "http://source" });

        Assert.Equal(["Triaged"], filters.TicketStatuses);
    }

    [Fact]
    public void Resolve_NullMeansNoRestriction_WhenNoDefaultExists()
    {
        JiraProcessingFilterResolver resolver = new();

        ResolvedJiraProcessingFilters filters = resolver.Resolve(new JiraProcessingOptions { AgentCliCommand = "agent {ticketKey}", JiraSourceAddress = "http://source" });

        Assert.Null(filters.TicketStatuses);
    }

    [Fact]
    public void Resolve_EmptyListOverridesDefaultToNoRestriction()
    {
        JiraProcessingFilterResolver resolver = new(new JiraProcessingFilterDefaults { TicketStatusesToProcess = ["Triaged"] });

        ResolvedJiraProcessingFilters filters = resolver.Resolve(new JiraProcessingOptions { AgentCliCommand = "agent {ticketKey}", JiraSourceAddress = "http://source", TicketStatusesToProcess = [] });

        Assert.Null(filters.TicketStatuses);
    }

    [Fact]
    public void Resolve_NonEmptyListRestrictsToProvidedValues()
    {
        JiraProcessingFilterResolver resolver = new(new JiraProcessingFilterDefaults { TicketStatusesToProcess = ["Triaged"] });

        ResolvedJiraProcessingFilters filters = resolver.Resolve(new JiraProcessingOptions { AgentCliCommand = "agent {ticketKey}", JiraSourceAddress = "http://source", TicketStatusesToProcess = ["Resolved - change required"] });

        Assert.Equal(["Resolved - change required"], filters.TicketStatuses);
    }

    [Fact]
    public void CreateLocalProcessingRequest_MapsShapeSeparatelyFromIssueType()
    {
        ResolvedJiraProcessingFilters filters = new()
        {
            TicketStatuses = ["Triaged"],
            Projects = ["FHIR"],
            WorkGroups = ["Infrastructure"],
            TicketTypes = ["Change Request"],
            SourceTicketShape = "fhir",
        };
        JiraLocalProcessingRequestFactory factory = new();

        FhirAugury.Common.Api.JiraLocalProcessingListRequest request = factory.CreateListRequest(filters);

        Assert.Equal(["Triaged"], request.Statuses);
        Assert.Equal(["FHIR"], request.Projects);
        Assert.Equal(["Infrastructure"], request.WorkGroups);
        Assert.Equal(["Change Request"], request.Types);
        Assert.Equal("fhir", filters.SourceTicketShape);
        Assert.False(request.ProcessedLocally);
    }

    [Fact]
    public void Resolve_Specifications_RoundTripFromOptions()
    {
        JiraProcessingFilterResolver resolver = new();

        ResolvedJiraProcessingFilters filters = resolver.Resolve(new JiraProcessingOptions
        {
            AgentCliCommand = "agent {ticketKey}",
            JiraSourceAddress = "http://source",
            SpecificationsToInclude = ["fhir-core", "fhir-extensions"],
        });

        Assert.Equal(["fhir-core", "fhir-extensions"], filters.Specifications);
    }

    [Fact]
    public void Resolve_Specifications_FallsBackToDefault_WhenOptionsNull()
    {
        JiraProcessingFilterResolver resolver = new(new JiraProcessingFilterDefaults { SpecificationsToInclude = ["fhir-core"] });

        ResolvedJiraProcessingFilters filters = resolver.Resolve(new JiraProcessingOptions { AgentCliCommand = "agent {ticketKey}", JiraSourceAddress = "http://source" });

        Assert.Equal(["fhir-core"], filters.Specifications);
    }

    [Fact]
    public void CreateLocalProcessingRequest_MapsSpecifications()
    {
        ResolvedJiraProcessingFilters filters = new()
        {
            Specifications = ["fhir-core", "fhir-extensions"],
            SourceTicketShape = "fhir",
        };
        JiraLocalProcessingRequestFactory factory = new();

        FhirAugury.Common.Api.JiraLocalProcessingListRequest request = factory.CreateListRequest(filters);

        Assert.Equal(["fhir-core", "fhir-extensions"], request.Specifications);
    }

    public static TheoryData<List<string>?, List<string>?> InactiveLabelLists => new()
    {
        { null, null },
        { [], [] },
        { null, [] },
        { [], null },
        { [null!, "", " \t"], ["", "\r\n", null!] },
    };

    [Theory]
    [MemberData(nameof(InactiveLabelLists))]
    public void Resolve_LabelListsHaveNoDefaults(List<string>? includes, List<string>? excludes)
    {
        JiraProcessingFilterResolver resolver = new(new JiraProcessingFilterDefaults
        {
            TicketStatusesToProcess = ["Triaged"],
            ProjectsToInclude = ["FHIR"],
            SpecificationsToInclude = ["fhir-core"],
            WorkGroupsToInclude = ["FHIR-I"],
            TicketTypesToProcess = ["Change Request"],
        });
        JiraProcessingOptions options = new()
        {
            LabelsToInclude = includes,
            LabelsToExclude = excludes,
            ProjectsToInclude = ["PSS"],
            WorkGroupsToInclude = [],
        };

        ResolvedJiraProcessingFilters filters = resolver.Resolve(options);

        Assert.Null(new JiraProcessingOptions().LabelsToInclude);
        Assert.Null(new JiraProcessingOptions().LabelsToExclude);
        Assert.Null(filters.LabelsToInclude);
        Assert.Null(filters.LabelsToExclude);
        Assert.False(filters.HasLabelTextFilters);
        Assert.Equal(["Triaged"], filters.TicketStatuses);
        Assert.Equal(["PSS"], filters.Projects);
        Assert.Equal(["fhir-core"], filters.Specifications);
        Assert.Null(filters.WorkGroups);
        Assert.Equal(["Change Request"], filters.TicketTypes);
    }

    [Fact]
    public void Resolve_LabelListsIgnoreOnlyBlankEntries()
    {
        string[] values = [" inc-01 ", "%", "_", "O'Reilly", @"back\slash", "MiXeD", "mixed", "MiXeD", "é"];
        JiraProcessingOptions options = new()
        {
            LabelsToInclude = [null!, "", " ", "\t\r\n", .. values],
            LabelsToExclude = [.. values, "", null!, "\t"],
            TicketStatusesToProcess = [" ", ""],
            ProjectsToInclude = [" FHIR ", "FHIR", "FHIR"],
        };

        ResolvedJiraProcessingFilters filters = new JiraProcessingFilterResolver().Resolve(options);

        Assert.Equal(values, filters.LabelsToInclude);
        Assert.Equal(values, filters.LabelsToExclude);
        Assert.True(filters.HasLabelTextFilters);
        Assert.NotNull(filters.TicketStatuses);
        Assert.Empty(filters.TicketStatuses);
        Assert.Equal([" FHIR ", "FHIR", "FHIR"], filters.Projects);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Resolve_LabelListsAreIndependentAndCopied(bool include, bool exclude)
    {
        List<string>? includes = include ? [" inc-01 ", "inc-01"] : null;
        List<string>? excludes = exclude ? ["ex_%", "ex_%"] : [];
        JiraProcessingOptions options = new()
        {
            LabelsToInclude = includes,
            LabelsToExclude = excludes,
        };
        JiraProcessingFilterResolver resolver = new();

        ResolvedJiraProcessingFilters filters = resolver.Resolve(options);
        includes?.Clear();
        excludes?.Add("changed");
        options.LabelsToInclude = ["replacement"];
        options.LabelsToExclude = ["replacement"];

        Assert.Equal(include ? [" inc-01 ", "inc-01"] : null, filters.LabelsToInclude);
        Assert.Equal(exclude ? ["ex_%", "ex_%"] : null, filters.LabelsToExclude);
        Assert.True(filters.HasLabelTextFilters);
        Assert.Null(resolver.Resolve(new JiraProcessingOptions()).LabelsToInclude);
        Assert.Null(resolver.Resolve(new JiraProcessingOptions()).LabelsToExclude);
    }

    [Theory]
    [MemberData(nameof(InactiveLabelLists))]
    public void HasLabelTextFilters_RequiresUsableEntries(List<string>? includes, List<string>? excludes)
    {
        ResolvedJiraProcessingFilters filters = new()
        {
            LabelsToInclude = includes,
            LabelsToExclude = excludes,
        };

        Assert.False(filters.HasLabelTextFilters);
        Assert.True((filters with { LabelsToInclude = [" % "] }).HasLabelTextFilters);
        Assert.True((filters with { LabelsToExclude = ["_"] }).HasLabelTextFilters);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateSelectionRequest_PreservesConfiguredFacetsAndRunMode(bool runBacked)
    {
        ResolvedJiraProcessingFilters filters = new()
        {
            TicketStatuses = ["Triaged"],
            Projects = ["FHIR"],
            Specifications = ["fhir-core"],
            WorkGroups = ["FHIR-I"],
            TicketTypes = ["Change Request"],
            LabelsToInclude = [" inc_% ", "inc-02", "inc-02"],
            LabelsToExclude = ["ex-01"],
            SourceTicketShape = "pss",
        };

        JiraLocalProcessingSelectionRequest request = new JiraLocalProcessingRequestFactory()
            .CreateSelectionRequest(filters, limit: 500, offset: 1000, runBacked: runBacked);

        Assert.Equal(filters.TicketStatuses, request.Statuses);
        Assert.Equal(filters.Projects, request.Projects);
        Assert.Equal(filters.Specifications, request.Specifications);
        Assert.Equal(filters.WorkGroups, request.WorkGroups);
        Assert.Equal(filters.TicketTypes, request.Types);
        Assert.Equal(runBacked ? (bool?)null : false, request.ProcessedLocally);
        Assert.Equal(filters.LabelsToInclude, request.LabelText!.Includes);
        Assert.Equal(filters.LabelsToExclude, request.LabelText.Excludes);
        Assert.NotSame(filters.LabelsToInclude, request.LabelText.Includes);
        Assert.NotSame(filters.LabelsToExclude, request.LabelText.Excludes);
        Assert.Null(request.Labels);
        Assert.Null(request.Keys);
        Assert.Equal(500, request.Limit);
        Assert.Equal(1000, request.Offset);
        Assert.Equal("pss", filters.SourceTicketShape);
    }

    [Fact]
    public void CreateLabelMatchRequest_ContainsOnlyKeysAndLabels()
    {
        List<string> keys = ["FHIR-1", "FHIR-2"];
        ResolvedJiraProcessingFilters filters = new()
        {
            TicketStatuses = ["Triaged"],
            Projects = ["FHIR"],
            Specifications = ["fhir-core"],
            WorkGroups = ["FHIR-I"],
            TicketTypes = ["Change Request"],
            LabelsToInclude = [" inc_% ", "inc-01"],
            LabelsToExclude = ["ex-01", "ex-01"],
            SourceTicketShape = "pss",
        };

        JiraLocalProcessingSelectionRequest request = new JiraLocalProcessingRequestFactory()
            .CreateLabelMatchRequest(keys, filters);
        keys.Clear();

        Assert.Equal(["FHIR-1", "FHIR-2"], request.Keys);
        Assert.Equal(filters.LabelsToInclude, request.LabelText!.Includes);
        Assert.Equal(filters.LabelsToExclude, request.LabelText.Excludes);
        Assert.NotSame(filters.LabelsToInclude, request.LabelText.Includes);
        Assert.NotSame(filters.LabelsToExclude, request.LabelText.Excludes);
        Assert.Equal(500, request.Limit);
        Assert.Equal(0, request.Offset);
        Assert.Null(request.Statuses);
        Assert.Null(request.Projects);
        Assert.Null(request.Specifications);
        Assert.Null(request.WorkGroups);
        Assert.Null(request.Types);
        Assert.Null(request.Labels);
        Assert.Null(request.ProcessedLocally);
        Assert.Null(request.Priorities);
        Assert.Null(request.ChangeCategories);
        Assert.Null(request.ChangeImpacts);
        Assert.Null(request.RelatedArtifacts);
        Assert.Null(request.Reporters);
        Assert.Equal("pss", filters.SourceTicketShape);
    }

    [Fact]
    public void CreateLabelMatchRequest_RejectsEmptyOrOversizedBatches()
    {
        JiraLocalProcessingRequestFactory factory = new();
        ResolvedJiraProcessingFilters filters = new() { LabelsToInclude = ["inc-01"] };

        Assert.Throws<ArgumentOutOfRangeException>(() => factory.CreateLabelMatchRequest([], filters));
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.CreateLabelMatchRequest(
            Enumerable.Range(1, 501).Select(index => $"FHIR-{index}").ToArray(),
            filters));
        Assert.Throws<ArgumentException>(() => factory.CreateLabelMatchRequest(["FHIR-1", "fhir-1"], filters));
        Assert.Throws<ArgumentException>(() => factory.CreateLabelMatchRequest([" "], filters));
    }
}
