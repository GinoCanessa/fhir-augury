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
}
