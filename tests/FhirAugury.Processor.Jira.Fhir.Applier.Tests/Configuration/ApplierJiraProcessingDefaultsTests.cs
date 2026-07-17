using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Configuration;

public class ApplierJiraProcessingDefaultsTests
{
    [Fact]
    public void Apply_FillsRequiredJiraDefaults()
    {
        JiraProcessingOptions options = new();

        ApplierJiraProcessingDefaults.Apply(options);

        Assert.NotNull(options.TicketStatusesToProcess);
        Assert.Contains("Resolved - change required", options.TicketStatusesToProcess!);
        Assert.NotNull(options.TicketTypesToProcess);
        Assert.Contains("Change Request", options.TicketTypesToProcess!);
        Assert.False(string.IsNullOrEmpty(options.AgentCliCommand));
        Assert.False(string.IsNullOrEmpty(options.JiraSourceAddress));
    }

    [Fact]
    public void Apply_DoesNotOverrideExistingValues()
    {
        JiraProcessingOptions options = new()
        {
            TicketStatusesToProcess = ["Reviewed"],
            AgentCliCommand = "custom {ticketKey} {dbPath} {worktreePath} {plannerDbPath} {repoOwner} {repoName}",
            JiraSourceAddress = "http://override:1234",
        };

        ApplierJiraProcessingDefaults.Apply(options);

        Assert.Equal(new[] { "Reviewed" }, options.TicketStatusesToProcess);
        Assert.Equal("custom {ticketKey} {dbPath} {worktreePath} {plannerDbPath} {repoOwner} {repoName}", options.AgentCliCommand);
        Assert.Equal("http://override:1234", options.JiraSourceAddress);
    }

    [Fact]
    public void Validate_ReturnsErrorWhenAgentCliMissingRequiredToken()
    {
        JiraProcessingOptions options = new()
        {
            AgentCliCommand = "copilot run --ticket {ticketKey}",
            JiraSourceAddress = "http://localhost",
        };

        List<string> errors = ApplierJiraProcessingDefaults.Validate(options).ToList();

        Assert.Contains(errors, e => e.Contains("{worktreePath}"));
        Assert.Contains(errors, e => e.Contains("{plannerDbPath}"));
        Assert.Contains(errors, e => e.Contains("{repoOwner}"));
        Assert.Contains(errors, e => e.Contains("{repoName}"));
    }

    [Fact]
    public void Validate_PassesForApplyOnlyDefaults()
    {
        JiraProcessingOptions options = new();
        ApplierJiraProcessingDefaults.Apply(options);

        List<string> errors = ApplierJiraProcessingDefaults.Validate(options).ToList();

        Assert.Empty(errors);
    }
}
