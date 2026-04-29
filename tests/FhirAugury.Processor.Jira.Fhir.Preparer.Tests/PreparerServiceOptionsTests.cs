using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparerServiceOptionsTests
{
    [Fact]
    public void Defaults_UseTriagedStatusAndPort5171()
    {
        PreparerServiceOptions processing = new();
        JiraProcessingOptions jira = new();
        PreparerJiraProcessingDefaults.Apply(jira);

        Assert.Equal(5171, processing.Ports.Http);
        Assert.Equal(4, processing.MaxConcurrentProcessingThreads);
        Assert.Equal(["Triaged"], jira.TicketStatusesToProcess);
        Assert.Equal(PreparerJiraProcessingDefaults.AgentCliCommand, jira.AgentCliCommand);
    }

    [Fact]
    public void Validate_RejectsAgentCommandMissingTicketKeyToken()
    {
        JiraProcessingOptions options = new()
        {
            AgentCliCommand = "copilot run ticket-prep --db {dbPath}",
            JiraSourceAddress = PreparerJiraProcessingDefaults.JiraSourceAddress,
        };

        List<string> errors = PreparerJiraProcessingDefaults.Validate(options).ToList();

        Assert.Contains(errors, error => error.Contains("{ticketKey}", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsAgentCommandMissingDbPathToken()
    {
        JiraProcessingOptions options = new()
        {
            AgentCliCommand = "copilot run ticket-prep --ticket {ticketKey}",
            JiraSourceAddress = PreparerJiraProcessingDefaults.JiraSourceAddress,
        };

        List<string> errors = PreparerJiraProcessingDefaults.Validate(options).ToList();

        Assert.Contains(errors, error => error.Contains("{dbPath}", StringComparison.Ordinal));
    }

    [Fact]
    public void FilterLists_NullAndEmptyRemainDistinct()
    {
        JiraProcessingOptions nullOptions = new();
        PreparerJiraProcessingDefaults.Apply(nullOptions);
        JiraProcessingOptions emptyOptions = new()
        {
            TicketStatusesToProcess = [],
            ProjectsToInclude = [],
            WorkGroupsToInclude = [],
            TicketTypesToProcess = [],
        };
        PreparerJiraProcessingDefaults.Apply(emptyOptions);

        Assert.Equal(["Triaged"], nullOptions.TicketStatusesToProcess);
        Assert.Empty(emptyOptions.TicketStatusesToProcess);
        Assert.Empty(emptyOptions.ProjectsToInclude!);
        Assert.Empty(emptyOptions.WorkGroupsToInclude!);
        Assert.Empty(emptyOptions.TicketTypesToProcess!);
    }
}
