using FhirAugury.Processing.Common.Configuration;

namespace FhirAugury.Processing.Common.Tests.Configuration;

public class ProcessingServiceOptionsTests
{
    [Fact]
    public void Defaults_IncludeProcessingPortAndStartupBehavior()
    {
        ProcessingServiceOptions options = new();

        Assert.Equal("./data/processing.db", options.DatabasePath);
        Assert.Equal("00:05:00", options.SyncSchedule);
        Assert.Equal(1, options.MaxConcurrentProcessingThreads);
        Assert.True(options.StartProcessingOnStartup);
        Assert.Equal(5170, options.Ports.Http);
        Assert.Equal("00:10:00", options.OrphanedInProgressThreshold);
        Assert.Equal("00:01:00", options.AuthoringRetryDelay);
        Assert.Equal(3, options.AuthoringMaxAttempts);
        Assert.Equal("./data/snapshots", options.SnapshotDirectory);
        Assert.Equal(1, options.SnapshotSchemaVersion);
        Assert.True(options.ReconcileSnapshotsOnStartup);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Validate_RejectsInvalidConcurrency()
    {
        ProcessingServiceOptions options = new() { MaxConcurrentProcessingThreads = 0 };

        Assert.Contains(options.Validate(), e => e.Contains("MaxConcurrentProcessingThreads", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-timespan")]
    [InlineData("00:00:00")]
    public void Validate_RejectsInvalidSyncSchedule(string syncSchedule)
    {
        ProcessingServiceOptions options = new() { SyncSchedule = syncSchedule };

        Assert.Contains(options.Validate(), e => e.Contains("SyncSchedule", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-timespan")]
    [InlineData("00:00:00")]
    public void Validate_RejectsInvalidOrphanedInProgressThreshold(string threshold)
    {
        ProcessingServiceOptions options = new() { OrphanedInProgressThreshold = threshold };

        Assert.Contains(options.Validate(), e => e.Contains("OrphanedInProgressThreshold", StringComparison.Ordinal));
    }

    [Fact]
    public void OrchestratorAddress_IsRetainedForFutureNotifications()
    {
        ProcessingServiceOptions options = new() { OrchestratorAddress = "http://localhost:5150" };

        Assert.Equal("http://localhost:5150", options.OrchestratorAddress);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-timespan")]
    [InlineData("00:00:00")]
    public void Validate_RejectsInvalidAuthoringRetryDelay(string retryDelay)
    {
        ProcessingServiceOptions options = new() { AuthoringRetryDelay = retryDelay };

        Assert.Contains(options.Validate(), error => error.Contains("AuthoringRetryDelay", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsInvalidAuthoringAndSnapshotValues()
    {
        ProcessingServiceOptions options = new()
        {
            AuthoringMaxAttempts = 0,
            SnapshotDirectory = "",
            SnapshotSchemaVersion = 0,
        };

        string[] errors = options.Validate().ToArray();
        Assert.Contains(errors, error => error.Contains("AuthoringMaxAttempts", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("SnapshotDirectory", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("SnapshotSchemaVersion", StringComparison.Ordinal));
    }
}
