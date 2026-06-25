using FhirAugury.Common.Text;
using FhirAugury.Source.GitHub.Configuration;

namespace FhirAugury.Source.GitHub.Tests;

public class GitHubServiceOptionsTests
{
    [Fact]
    public void ResolveJiraScope_FhirCoreRepo_UsesFhirRange()
    {
        GitHubServiceOptions options = new();

        RepoJiraScope? scope = options.ResolveJiraScope("HL7/fhir");

        RepoJiraProjectScope project = Assert.Single(Assert.IsType<RepoJiraScope>(scope).Projects);
        Assert.Equal("FHIR", project.ProjectKey);
        Assert.Equal(2839, project.Lower);
        Assert.Equal(70000, project.Upper);
    }

    [Fact]
    public void ResolveJiraScope_UtgRepo_DefaultsToUp()
    {
        GitHubServiceOptions options = new();

        RepoJiraScope? scope = options.ResolveJiraScope("HL7/UTG");

        RepoJiraProjectScope project = Assert.Single(Assert.IsType<RepoJiraScope>(scope).Projects);
        Assert.Equal("UP", project.ProjectKey);
    }

    [Fact]
    public void ResolveJiraScope_UtgRepo_TerminologyOverride_SelectsUpsm()
    {
        GitHubServiceOptions options = new();
        options.RepoOverrides["HL7/UTG"] = new RepoOverrideOptions { TerminologyProjectKey = "UPSM" };

        RepoJiraScope? scope = options.ResolveJiraScope("HL7/UTG");

        RepoJiraProjectScope project = Assert.Single(Assert.IsType<RepoJiraScope>(scope).Projects);
        Assert.Equal("UPSM", project.ProjectKey);
        Assert.Equal(10, project.Lower);
        Assert.Equal(2000, project.Upper);
    }

    [Fact]
    public void ResolveJiraScope_ExplicitProjectKeyOverride_Wins()
    {
        GitHubServiceOptions options = new();
        options.RepoOverrides["HL7/UTG"] = new RepoOverrideOptions
        {
            JiraProjectKey = "FHIR",
            TerminologyProjectKey = "UPSM",
        };

        RepoJiraScope? scope = options.ResolveJiraScope("HL7/UTG");

        RepoJiraProjectScope project = Assert.Single(Assert.IsType<RepoJiraScope>(scope).Projects);
        Assert.Equal("FHIR", project.ProjectKey);
    }

    [Fact]
    public void ResolveJiraScope_Disabled_ReturnsNull()
    {
        GitHubServiceOptions options = new() { BareNumberAttributionEnabled = false };

        Assert.Null(options.ResolveJiraScope("HL7/fhir"));
    }

    [Fact]
    public void ResolveJiraScope_UnknownRepo_ReturnsNull()
    {
        GitHubServiceOptions options = new();

        Assert.Null(options.ResolveJiraScope("HL7/not-configured"));
    }

    [Fact]
    public void ResolveJiraScope_KeyWithoutRange_ReturnsNull()
    {
        GitHubServiceOptions options = new();
        options.RepoOverrides["HL7/fhir"] = new RepoOverrideOptions { JiraProjectKey = "PSS" };

        Assert.Null(options.ResolveJiraScope("HL7/fhir"));
    }

    [Fact]
    public void ResolveJiraScope_AlwaysSingleProject()
    {
        GitHubServiceOptions options = new();

        RepoJiraScope? scope = options.ResolveJiraScope("HL7/fhir");

        Assert.Single(Assert.IsType<RepoJiraScope>(scope).Projects);
    }
}
