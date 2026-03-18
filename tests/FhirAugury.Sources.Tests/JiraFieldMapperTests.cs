using System.Text.Json;
using FhirAugury.Sources.Jira;

namespace FhirAugury.Sources.Tests;

public class JiraFieldMapperTests
{
    private static JsonElement LoadSampleIssue()
    {
        var json = File.ReadAllText(Path.Combine("TestData", "sample-jira-issue.json"));
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void MapIssue_ExtractsKey()
    {
        var issueJson = LoadSampleIssue();
        var record = JiraFieldMapper.MapIssue(issueJson);

        Assert.Equal("FHIR-43499", record.Key);
    }

    [Fact]
    public void MapIssue_ExtractsCoreFields()
    {
        var issueJson = LoadSampleIssue();
        var record = JiraFieldMapper.MapIssue(issueJson);

        Assert.Equal("FHIRPath normative readiness review", record.Title);
        Assert.Equal("FHIR", record.ProjectKey);
        Assert.Equal("Change Request", record.Type);
        Assert.Equal("Medium", record.Priority);
        Assert.Equal("Triaged", record.Status);
        Assert.Equal("Bryn Rhodes", record.Assignee);
        Assert.Equal("Gino Canessa", record.Reporter);
    }

    [Fact]
    public void MapIssue_ExtractsDescription()
    {
        var issueJson = LoadSampleIssue();
        var record = JiraFieldMapper.MapIssue(issueJson);

        Assert.NotNull(record.Description);
        Assert.Contains("FHIRPath specification", record.Description);
    }

    [Fact]
    public void MapIssue_ExtractsCustomField_Specification()
    {
        var issueJson = LoadSampleIssue();
        var record = JiraFieldMapper.MapIssue(issueJson);

        Assert.Equal("FHIRPath", record.Specification);
    }

    [Fact]
    public void MapIssue_ExtractsCustomField_WorkGroup()
    {
        var issueJson = LoadSampleIssue();
        var record = JiraFieldMapper.MapIssue(issueJson);

        Assert.Equal("FHIR Infrastructure", record.WorkGroup);
    }

    [Fact]
    public void MapIssue_ExtractsCustomField_RaisedInVersion()
    {
        var issueJson = LoadSampleIssue();
        var record = JiraFieldMapper.MapIssue(issueJson);

        Assert.Equal("STU3", record.RaisedInVersion);
    }

    [Fact]
    public void MapIssue_ExtractsCustomField_ResolutionDescription()
    {
        var issueJson = LoadSampleIssue();
        var record = JiraFieldMapper.MapIssue(issueJson);

        Assert.NotNull(record.ResolutionDescription);
        Assert.Contains("normative package", record.ResolutionDescription);
    }

    [Fact]
    public void MapIssue_ExtractsCustomField_RelatedArtifacts()
    {
        var issueJson = LoadSampleIssue();
        var record = JiraFieldMapper.MapIssue(issueJson);

        Assert.Equal("http://hl7.org/fhirpath/N1", record.RelatedArtifacts);
    }

    [Fact]
    public void MapIssue_ExtractsCustomField_Vote()
    {
        var issueJson = LoadSampleIssue();
        var record = JiraFieldMapper.MapIssue(issueJson);

        Assert.Equal("Affirmative", record.Vote);
    }

    [Fact]
    public void MapIssue_ExtractsLabels()
    {
        var issueJson = LoadSampleIssue();
        var record = JiraFieldMapper.MapIssue(issueJson);

        Assert.NotNull(record.Labels);
        Assert.Contains("fhirpath", record.Labels);
        Assert.Contains("normative", record.Labels);
    }

    [Fact]
    public void MapComments_ExtractsComments()
    {
        var issueJson = LoadSampleIssue();
        var comments = JiraFieldMapper.MapComments(issueJson, 1, "FHIR-43499");

        Assert.Equal(2, comments.Count);
        Assert.Equal("Bryn Rhodes", comments[0].Author);
        Assert.Contains("Initial review completed", comments[0].Body);
    }

    [Fact]
    public void MapIssue_HandlesNullResolution()
    {
        var issueJson = LoadSampleIssue();
        var record = JiraFieldMapper.MapIssue(issueJson);

        Assert.Null(record.Resolution);
        Assert.Null(record.ResolvedAt);
    }
}
