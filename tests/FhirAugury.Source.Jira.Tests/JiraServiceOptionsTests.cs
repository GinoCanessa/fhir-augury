using FhirAugury.Source.Jira.Configuration;

namespace FhirAugury.Source.Jira.Tests;

public class JiraServiceOptionsTests
{
    [Fact]
    public void GetEffectiveProjects_EmptyList_ReturnsSingleDefaultProject()
    {
        JiraServiceOptions opts = new() { DefaultProject = "FHIR" };

        List<JiraProjectConfig> result = opts.GetEffectiveProjects();

        Assert.Single(result);
        Assert.Equal("FHIR", result[0].Key);
    }

    [Fact]
    public void GetEffectiveProjects_PopulatedList_ReturnsEnabledOnly()
    {
        JiraServiceOptions opts = new()
        {
            Projects =
            [
                new JiraProjectConfig { Key = "FHIR" },
                new JiraProjectConfig { Key = "FHIR-I", Enabled = false },
                new JiraProjectConfig { Key = "CDA" },
            ]
        };

        List<JiraProjectConfig> result = opts.GetEffectiveProjects();

        Assert.Equal(2, result.Count);
        Assert.Equal("FHIR", result[0].Key);
        Assert.Equal("CDA", result[1].Key);
    }

    [Fact]
    public void GetEffectiveProjects_AllDisabled_ReturnsEmptyList()
    {
        JiraServiceOptions opts = new()
        {
            Projects =
            [
                new JiraProjectConfig { Key = "FHIR", Enabled = false },
                new JiraProjectConfig { Key = "CDA", Enabled = false },
            ]
        };

        List<JiraProjectConfig> result = opts.GetEffectiveProjects();

        Assert.Empty(result);
    }

    [Fact]
    public void GetEffectiveProjects_CustomDefaultProject_UsedAsFallback()
    {
        JiraServiceOptions opts = new() { DefaultProject = "CDA" };

        List<JiraProjectConfig> result = opts.GetEffectiveProjects();

        Assert.Single(result);
        Assert.Equal("CDA", result[0].Key);
    }

    [Fact]
    public void GetEffectiveProjects_PreservesJqlOverride()
    {
        JiraServiceOptions opts = new()
        {
            Projects =
            [
                new JiraProjectConfig { Key = "FHIR", Jql = "project = FHIR AND status = Open" },
            ]
        };

        List<JiraProjectConfig> result = opts.GetEffectiveProjects();

        Assert.Single(result);
        Assert.Equal("project = FHIR AND status = Open", result[0].Jql);
    }

    [Fact]
    public void GetEffectiveProjects_DefaultJqlIsNull()
    {
        JiraServiceOptions opts = new()
        {
            Projects = [new JiraProjectConfig { Key = "FHIR" }]
        };

        List<JiraProjectConfig> result = opts.GetEffectiveProjects();

        Assert.Null(result[0].Jql);
    }
}
