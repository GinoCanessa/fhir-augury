using FhirAugury.Common.Api;
using FhirAugury.Orchestrator.Configuration;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Tests;

public class OrchestratorOptionsTests
{
    private readonly OrchestratorOptionsValidator _validator = new();

    [Fact]
    public void TicketWorkflowCatalog_DefinesPreparerAndPlannerDependencies()
    {
        Assert.Equal(
            ["Jira"],
            TicketWorkflowDependencyCatalog.GetRequiredServices(
                TicketWorkflowDependencyCatalog.PreparerServiceName));
        Assert.Equal(
            ["Jira", "GitHub"],
            TicketWorkflowDependencyCatalog.GetRequiredServices(
                TicketWorkflowDependencyCatalog.PlannerServiceName));
    }

    [Fact]
    public void RequiredServices_MustReferenceKnownSources()
    {
        OrchestratorOptions options = CreateValidOptions();
        options.ProcessingServices["Planner"].RequiredServices =
            ["Jira", "Missing"];

        ValidateOptionsResult result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "unknown source service 'Missing'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void TicketWorkflowRequiredServices_MustMatchCatalog()
    {
        OrchestratorOptions options = CreateValidOptions();
        options.ProcessingServices["Planner"].RequiredServices = ["Jira"];

        ValidateOptionsResult result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(
                "must declare required services: Jira, GitHub",
                StringComparison.Ordinal));
    }

    [Fact]
    public void KnownDisabledDependency_DoesNotFailValidation()
    {
        OrchestratorOptions options = CreateValidOptions();
        options.Services["GitHub"].Enabled = false;

        ValidateOptionsResult result = _validator.Validate(null, options);

        Assert.Same(ValidateOptionsResult.Success, result);
    }

    private static OrchestratorOptions CreateValidOptions() => new()
    {
        Services = new Dictionary<string, SourceServiceConfig>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Jira"] = new()
            {
                HttpAddress = "http://jira",
                Enabled = true,
            },
            ["GitHub"] = new()
            {
                HttpAddress = "http://github",
                Enabled = true,
            },
        },
        ProcessingServices = new Dictionary<string, ProcessingServiceConfig>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Preparer"] = new()
            {
                HttpAddress = "http://preparer",
                RequiredServices = ["Jira"],
            },
            ["Planner"] = new()
            {
                HttpAddress = "http://planner",
                RequiredServices = ["Jira", "GitHub"],
            },
        },
    };
}
