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
}
