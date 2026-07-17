using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Hosting;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Hydration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HydrationSweeperHostedService = FhirAugury.Processor.Jira.Fhir.Preparer.Hosting.HydrationSweeperHostedService;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class HydrationSweeperHostedServiceTests
{
    [Fact]
    public async Task StartAsync_RunsSweepWhenEnabled()
    {
        RecordingSweeper sweeper = new();
        HydrationSweeperHostedService service = Create(sweeper, backfillOnStartup: true);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, sweeper.RunFullCalls);
        Assert.Equal(HydrationSweepReason.Startup, sweeper.LastReason);
    }

    [Fact]
    public async Task StartAsync_SkipsWhenBackfillOnStartupFalse()
    {
        RecordingSweeper sweeper = new();
        HydrationSweeperHostedService service = Create(sweeper, backfillOnStartup: false);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(0, sweeper.RunFullCalls);
    }

    [Fact]
    public async Task StartAsync_PropagatesUnavailableException()
    {
        SpecificationBackfillFailure failure = new("upstream unreachable", "ECONNREFUSED", null);
        ThrowingSweeper sweeper = new(new HydrationSweeperUnavailableException(failure));
        HydrationSweeperHostedService service = Create(sweeper, backfillOnStartup: true);

        await Assert.ThrowsAsync<HydrationSweeperUnavailableException>(
            () => service.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StopAsync_IsNoOp()
    {
        RecordingSweeper sweeper = new();
        HydrationSweeperHostedService service = Create(sweeper, backfillOnStartup: true);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, sweeper.RunFullCalls);
    }

    private static HydrationSweeperHostedService Create(PreparedHydrationSweeper sweeper, bool backfillOnStartup)
    {
        PreparerServiceOptions options = new();
        options.Hydration.BackfillOnStartup = backfillOnStartup;
        return new HydrationSweeperHostedService(
            sweeper,
            Options.Create(options),
            NullLogger<HydrationSweeperHostedService>.Instance);
    }

    private sealed class RecordingSweeper()
        : PreparedHydrationSweeper(
            new PreparedTicketHydrator(new HttpClient { BaseAddress = new Uri("http://unused/") }, null!, NullLogger<PreparedTicketHydrator>.Instance),
            new SpecificationBackfillService(new HttpClient { BaseAddress = new Uri("http://unused/") }, Options.Create(new HydrationOptions()), NullLogger<SpecificationBackfillService>.Instance),
            null!,
            Options.Create(new HydrationOptions()),
            NullLogger<PreparedHydrationSweeper>.Instance)
    {
        public int RunFullCalls { get; private set; }
        public HydrationSweepReason? LastReason { get; private set; }

        public override Task<HydrationSweepResult> RunFullAsync(HydrationSweepReason reason, CancellationToken ct)
        {
            RunFullCalls++;
            LastReason = reason;
            return Task.FromResult(new HydrationSweepResult(
                new SpecificationBackfillResult(0, 0, 0, null),
                PerTicketSweepResult.Empty,
                TimeSpan.Zero));
        }
    }

    private sealed class ThrowingSweeper(Exception ex)
        : PreparedHydrationSweeper(
            new PreparedTicketHydrator(new HttpClient { BaseAddress = new Uri("http://unused/") }, null!, NullLogger<PreparedTicketHydrator>.Instance),
            new SpecificationBackfillService(new HttpClient { BaseAddress = new Uri("http://unused/") }, Options.Create(new HydrationOptions()), NullLogger<SpecificationBackfillService>.Instance),
            null!,
            Options.Create(new HydrationOptions()),
            NullLogger<PreparedHydrationSweeper>.Instance)
    {
        public override Task<HydrationSweepResult> RunFullAsync(HydrationSweepReason reason, CancellationToken ct)
            => Task.FromException<HydrationSweepResult>(ex);
    }
}
