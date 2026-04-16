using FhirAugury.Source.Jira.Ingestion;

namespace FhirAugury.Source.Jira.Tests;

public class JiraSyncStateHelperTests
{
    [Fact]
    public void SyncKey_CombinesProjectAndRunType()
    {
        string result = JiraSyncStateHelper.SyncKey("FHIR", "full");
        Assert.Equal("FHIR:full", result);
    }

    [Fact]
    public void SyncKey_HandlesHyphenatedProject()
    {
        string result = JiraSyncStateHelper.SyncKey("FHIR-I", "incremental");
        Assert.Equal("FHIR-I:incremental", result);
    }

    [Fact]
    public void ParseSyncKey_SplitsCorrectly()
    {
        (string project, string runType) = JiraSyncStateHelper.ParseSyncKey("CDA:full");
        Assert.Equal("CDA", project);
        Assert.Equal("full", runType);
    }

    [Fact]
    public void ParseSyncKey_HyphenatedProject()
    {
        (string project, string runType) = JiraSyncStateHelper.ParseSyncKey("FHIR-I:incremental");
        Assert.Equal("FHIR-I", project);
        Assert.Equal("incremental", runType);
    }

    [Fact]
    public void ParseSyncKey_LegacyFormat_FallsBackToFhir()
    {
        (string project, string runType) = JiraSyncStateHelper.ParseSyncKey("full");
        Assert.Equal("FHIR", project);
        Assert.Equal("full", runType);
    }

    [Fact]
    public void ParseSyncKey_LegacyIncremental_FallsBackToFhir()
    {
        (string project, string runType) = JiraSyncStateHelper.ParseSyncKey("incremental");
        Assert.Equal("FHIR", project);
        Assert.Equal("incremental", runType);
    }

    [Theory]
    [InlineData("FHIR", "full")]
    [InlineData("FHIR-I", "incremental")]
    [InlineData("CDA", "rebuild")]
    [InlineData("V2", "full")]
    public void RoundTrip_SyncKey_ParseSyncKey(string project, string runType)
    {
        string key = JiraSyncStateHelper.SyncKey(project, runType);
        (string parsedProject, string parsedRunType) = JiraSyncStateHelper.ParseSyncKey(key);

        Assert.Equal(project, parsedProject);
        Assert.Equal(runType, parsedRunType);
    }
}
