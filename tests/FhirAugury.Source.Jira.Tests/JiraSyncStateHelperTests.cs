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

    [Theory]
    [InlineData("full")]
    [InlineData("incremental")]
    [InlineData("FULL")]
    public void ErrorFreeUpstreamRun_AdvancesWatermark(string runType)
    {
        DateTimeOffset previous = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset completed = previous.AddDays(1);
        IngestionResult result = Result(completed);

        DateTimeOffset? next = JiraSyncStateHelper.ComputeNextLastSuccessfulSyncAt(
            previous,
            runType,
            result);

        Assert.Equal(completed, next);
    }

    [Fact]
    public void PartialSuccess_PreservesPriorWatermark()
    {
        DateTimeOffset previous = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        IngestionResult result = Result(
            previous.AddDays(1),
            itemsFailed: 1,
            errors: ["one page failed"]);

        DateTimeOffset? next = JiraSyncStateHelper.ComputeNextLastSuccessfulSyncAt(
            previous,
            "incremental",
            result);

        Assert.Equal(previous, next);
    }

    [Fact]
    public void ThrownFailure_PreservesPriorWatermark()
    {
        DateTimeOffset previous = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        DateTimeOffset? next = JiraSyncStateHelper.ComputeNextLastSuccessfulSyncAt(
            previous,
            "full",
            result: null);

        Assert.Equal(previous, next);
    }

    [Fact]
    public void RebuildPreservesKnownWatermarkWithoutAdvancingIt()
    {
        DateTimeOffset previous = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        IngestionResult result = Result(previous.AddDays(7));

        DateTimeOffset? next = JiraSyncStateHelper.ComputeNextLastSuccessfulSyncAt(
            previous,
            "rebuild",
            result);

        Assert.Equal(previous, next);
    }

    [Fact]
    public void UnrecognizedRunType_DoesNotManufactureWatermark()
    {
        IngestionResult result = Result(
            new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));

        DateTimeOffset? next = JiraSyncStateHelper.ComputeNextLastSuccessfulSyncAt(
            previous: null,
            "cache",
            result);

        Assert.Null(next);
    }

    [Fact]
    public void OlderSuccessfulCompletion_DoesNotRegressWatermark()
    {
        DateTimeOffset previous = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);
        IngestionResult result = Result(previous.AddDays(-1));

        DateTimeOffset? next = JiraSyncStateHelper.ComputeNextLastSuccessfulSyncAt(
            previous,
            "full",
            result);

        Assert.Equal(previous, next);
    }

    private static IngestionResult Result(
        DateTimeOffset completedAt,
        int itemsFailed = 0,
        List<string>? errors = null)
        => new IngestionResult(
            ItemsProcessed: 1,
            ItemsNew: 1,
            ItemsUpdated: 0,
            ItemsFailed: itemsFailed,
            Errors: errors ?? [],
            StartedAt: completedAt.AddMinutes(-1))
        {
            CompletedAt = completedAt,
        };
}
