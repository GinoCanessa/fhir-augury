using FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;
using FhirAugury.Processor.Jira.Fhir.Preparer.Hydration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class HydrationAdminControllerTests
{
    [Fact]
    public async Task Backfill_SpecBackfillFails_Returns503_AndDoesNotFirePerTicketSweep()
    {
        SpecificationBackfillFailure failure = new("Jira source unreachable; reason here", "ECONNREFUSED", null);
        FakeSweeper sweeper = new(
            specResult: new SpecificationBackfillResult(0, 0, 0, failure));
        HydrationAdminController controller = Create(sweeper);

        IActionResult result = await controller.TriggerBackfill(CancellationToken.None);

        ObjectResult obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, obj.StatusCode);
        ProblemDetails problem = Assert.IsAssignableFrom<ProblemDetails>(obj.Value);
        Assert.Contains("unreachable", problem.Detail, StringComparison.OrdinalIgnoreCase);
        // Per-ticket sweep must never start when spec backfill fails.
        Assert.Equal(0, sweeper.PerTicketCalls);
    }

    [Fact]
    public async Task Backfill_SpecBackfillSucceeds_Returns202_AndFiresPerTicketSweep()
    {
        FakeSweeper sweeper = new(
            specResult: new SpecificationBackfillResult(3, 1, 2, null));
        HydrationAdminController controller = Create(sweeper);

        IActionResult result = await controller.TriggerBackfill(CancellationToken.None);

        AcceptedResult accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Equal(StatusCodes.Status202Accepted, accepted.StatusCode);
        string body = System.Text.Json.JsonSerializer.Serialize(accepted.Value);
        Assert.Contains("\"updated\":3", body);
        Assert.Contains("\"stillEmpty\":1", body);
        Assert.Contains("\"notFound\":2", body);

        // The per-ticket sweep is fire-and-forget; wait for it to run.
        await sweeper.WaitForPerTicketAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, sweeper.PerTicketCalls);
    }

    [Fact]
    public async Task Backfill_SpecBackfillEmptyKeyset_StillReturns202()
    {
        // Short-circuit: no empty Specification rows. Failure is null but no work was done.
        FakeSweeper sweeper = new(
            specResult: new SpecificationBackfillResult(0, 0, 0, null));
        HydrationAdminController controller = Create(sweeper);

        IActionResult result = await controller.TriggerBackfill(CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
    }

    private static HydrationAdminController Create(PreparedHydrationSweeper sweeper)
    {
        return new HydrationAdminController(
            sweeper,
            NullLogger<HydrationAdminController>.Instance,
            new StubLifetime());
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class FakeSweeper(SpecificationBackfillResult specResult)
        : PreparedHydrationSweeper(
            new PreparedTicketHydrator(new HttpClient { BaseAddress = new Uri("http://unused/") }, null!, NullLogger<PreparedTicketHydrator>.Instance),
            new SpecificationBackfillService(new HttpClient { BaseAddress = new Uri("http://unused/") }, Options.Create(new HydrationOptions()), NullLogger<SpecificationBackfillService>.Instance),
            null!,
            Options.Create(new HydrationOptions()),
            NullLogger<PreparedHydrationSweeper>.Instance)
    {
        private readonly TaskCompletionSource<bool> _perTicketSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _perTicketCalls;

        public int PerTicketCalls => _perTicketCalls;

        public override Task<SpecificationBackfillResult> RunSpecificationBackfillAsync(CancellationToken ct)
            => Task.FromResult(specResult);

        public override Task<PerTicketSweepResult> RunPerTicketSweepAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _perTicketCalls);
            _perTicketSignal.TrySetResult(true);
            return Task.FromResult(PerTicketSweepResult.Empty);
        }

        public Task WaitForPerTicketAsync(TimeSpan timeout)
        {
            return Task.WhenAny(_perTicketSignal.Task, Task.Delay(timeout));
        }
    }
}
