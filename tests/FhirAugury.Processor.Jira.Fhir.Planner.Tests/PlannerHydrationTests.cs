using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Configuration;
using FhirAugury.Processor.Jira.Fhir.Planner.Controllers;
using FhirAugury.Processor.Jira.Fhir.Planner.Hydration;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HydrationSweeperHostedService = FhirAugury.Processor.Jira.Fhir.Planner.Hosting.HydrationSweeperHostedService;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannedHydrationSweeperTests
{
    [Fact]
    public async Task RunFull_Startup_ThrowsWhenSpecBackfillFails()
    {
        using DatabaseFixture fixture = new();
        StubBackfill backfill = new(new SpecificationBackfillResult(0, 0, 0, new SpecificationBackfillFailure("upstream down", "ECONNREFUSED", null)));
        StubHydrator hydrator = new(fixture.Database);
        PlannedHydrationSweeper sweeper = CreateSweeper(fixture.Database, hydrator, backfill);
        await Assert.ThrowsAsync<HydrationSweeperUnavailableException>(
            () => sweeper.RunFullAsync(HydrationSweepReason.Startup, CancellationToken.None));
    }

    [Fact]
    public async Task RunFull_AdminRequest_ReturnsFailureWithoutThrowing()
    {
        using DatabaseFixture fixture = new();
        StubBackfill backfill = new(new SpecificationBackfillResult(0, 0, 0, new SpecificationBackfillFailure("upstream down", "ECONNREFUSED", null)));
        StubHydrator hydrator = new(fixture.Database);
        PlannedHydrationSweeper sweeper = CreateSweeper(fixture.Database, hydrator, backfill);
        HydrationSweepResult result = await sweeper.RunFullAsync(HydrationSweepReason.AdminRequest, CancellationToken.None);
        Assert.NotNull(result.Specification.Failure);
    }

    [Fact]
    public async Task PerTicketSweep_NoEligibleRows_IsNoOp()
    {
        using DatabaseFixture fixture = new();
        StubBackfill backfill = new(new SpecificationBackfillResult(0, 0, 0, null));
        StubHydrator hydrator = new(fixture.Database);
        PlannedHydrationSweeper sweeper = CreateSweeper(fixture.Database, hydrator, backfill);
        PerTicketSweepResult result = await sweeper.RunPerTicketSweepAsync(CancellationToken.None);
        Assert.Equal(0, result.Eligible);
        Assert.Equal(0, hydrator.Calls);
    }

    private static PlannedHydrationSweeper CreateSweeper(PlannerDatabase database, StubHydrator hydrator, StubBackfill backfill)
        => new(hydrator, backfill, database, Options.Create(new HydrationOptions()), NullLogger<PlannedHydrationSweeper>.Instance);

    private sealed class StubBackfill(SpecificationBackfillResult result)
        : SpecificationBackfillService(new HttpClient { BaseAddress = new Uri("http://unused/") }, Options.Create(new HydrationOptions()), NullLogger<SpecificationBackfillService>.Instance)
    {
        public override Task<SpecificationBackfillResult> RunAsync(string processorDbPath, CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class StubHydrator(PlannerDatabase database)
        : PlannedTicketHydrator(new HttpClient { BaseAddress = new Uri("http://unused/") }, database, NullLogger<PlannedTicketHydrator>.Instance)
    {
        public int Calls { get; private set; }
        public override Task HydrateAsync(string issueKey, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _dir;
        public PlannerDatabase Database { get; }
        public DatabaseFixture()
        {
            _dir = Path.Combine(Environment.CurrentDirectory, "temp", "planned-sweeper", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Database = new PlannerDatabase(Path.Combine(_dir, "planner.db"), NullLogger<PlannerDatabase>.Instance);
            Database.Initialize();
        }
        public void Dispose()
        {
            Database.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}

public sealed class PlannerHydrationSweeperHostedServiceTests
{
    [Fact]
    public async Task StartAsync_RespectsBackfillOnStartupFalse()
    {
        using DatabaseFixture fixture = new();
        RecordingSweeper sweeper = new(fixture.Database);
        PlannerServiceOptions opts = new();
        opts.Hydration.BackfillOnStartup = false;
        HydrationSweeperHostedService service = new(sweeper, Options.Create(opts), NullLogger<HydrationSweeperHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(0, sweeper.RunFullCalls);
    }

    private sealed class RecordingSweeper(PlannerDatabase db)
        : PlannedHydrationSweeper(
            new PlannedTicketHydrator(new HttpClient { BaseAddress = new Uri("http://unused/") }, db, NullLogger<PlannedTicketHydrator>.Instance),
            new StubBackfill(),
            db,
            Options.Create(new HydrationOptions()),
            NullLogger<PlannedHydrationSweeper>.Instance)
    {
        public int RunFullCalls { get; private set; }
        public override Task<HydrationSweepResult> RunFullAsync(HydrationSweepReason reason, CancellationToken ct)
        {
            RunFullCalls++;
            return Task.FromResult(new HydrationSweepResult(new SpecificationBackfillResult(0, 0, 0, null), PerTicketSweepResult.Empty, TimeSpan.Zero));
        }
    }

    private sealed class StubBackfill()
        : SpecificationBackfillService(new HttpClient { BaseAddress = new Uri("http://unused/") }, Options.Create(new HydrationOptions()), NullLogger<SpecificationBackfillService>.Instance)
    {
        public override Task<SpecificationBackfillResult> RunAsync(string processorDbPath, CancellationToken ct)
            => Task.FromResult(new SpecificationBackfillResult(0, 0, 0, null));
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _dir;
        public PlannerDatabase Database { get; }
        public DatabaseFixture()
        {
            _dir = Path.Combine(Environment.CurrentDirectory, "temp", "planner-hosted", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Database = new PlannerDatabase(Path.Combine(_dir, "planner.db"), NullLogger<PlannerDatabase>.Instance);
            Database.Initialize();
        }
        public void Dispose()
        {
            Database.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}

public sealed class PlannerHydrationAdminControllerTests
{
    [Fact]
    public async Task TriggerBackfill_Returns503_OnSpecFailure()
    {
        using DatabaseFixture fixture = new();
        FakeSweeper sweeper = new(fixture.Database, specResult: new SpecificationBackfillResult(0, 0, 0, new SpecificationBackfillFailure("oops", "ECONNREFUSED", null)));
        HydrationAdminController controller = Create(sweeper);
        IActionResult result = await controller.TriggerBackfill(CancellationToken.None);
        ObjectResult problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
    }

    [Fact]
    public async Task TriggerBackfill_Returns202_OnSuccess()
    {
        using DatabaseFixture fixture = new();
        FakeSweeper sweeper = new(fixture.Database, specResult: new SpecificationBackfillResult(2, 0, 0, null));
        HydrationAdminController controller = Create(sweeper);
        IActionResult result = await controller.TriggerBackfill(CancellationToken.None);
        AcceptedResult accepted = Assert.IsType<AcceptedResult>(result);
        Assert.NotNull(accepted.Value);
    }

    private static HydrationAdminController Create(PlannedHydrationSweeper sweeper)
        => new(sweeper, NullLogger<HydrationAdminController>.Instance, new FakeLifetime());

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopping { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopped { get; } = CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class FakeSweeper(PlannerDatabase db, SpecificationBackfillResult specResult)
        : PlannedHydrationSweeper(
            new PlannedTicketHydrator(new HttpClient { BaseAddress = new Uri("http://unused/") }, db, NullLogger<PlannedTicketHydrator>.Instance),
            new StubBackfill(),
            db,
            Options.Create(new HydrationOptions()),
            NullLogger<PlannedHydrationSweeper>.Instance)
    {
        private readonly SpecificationBackfillResult _spec = specResult;
        public override Task<SpecificationBackfillResult> RunSpecificationBackfillAsync(CancellationToken ct) => Task.FromResult(_spec);
        public override Task<PerTicketSweepResult> RunPerTicketSweepAsync(CancellationToken ct) => Task.FromResult(PerTicketSweepResult.Empty);
    }

    private sealed class StubBackfill()
        : SpecificationBackfillService(new HttpClient { BaseAddress = new Uri("http://unused/") }, Options.Create(new HydrationOptions()), NullLogger<SpecificationBackfillService>.Instance)
    {
        public override Task<SpecificationBackfillResult> RunAsync(string processorDbPath, CancellationToken ct)
            => Task.FromResult(new SpecificationBackfillResult(0, 0, 0, null));
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _dir;
        public PlannerDatabase Database { get; }
        public DatabaseFixture()
        {
            _dir = Path.Combine(Environment.CurrentDirectory, "temp", "planner-admin", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Database = new PlannerDatabase(Path.Combine(_dir, "planner.db"), NullLogger<PlannerDatabase>.Instance);
            Database.Initialize();
        }
        public void Dispose()
        {
            Database.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}
