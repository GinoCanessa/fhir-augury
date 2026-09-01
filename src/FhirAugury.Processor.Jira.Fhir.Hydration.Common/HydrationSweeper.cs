using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Hydration.Common;

/// <summary>
/// Coordinates the two passes of a processor-service hydration sweep:
/// Specification backfill on <c>jira_processing_source_tickets</c>
/// followed by re-hydration of every processor-side ticket row whose
/// hydration row is missing or <c>HydrationStatus = 'unresolved'</c>.
/// Per-ticket fan-out is bounded by
/// <see cref="HydrationOptions.MaxParallelism"/> and writes are
/// serialized through a private semaphore so concurrent
/// <see cref="HydrationCoordinator.HydrateAsync"/> calls cannot
/// collide on the SQLite WAL writer. The coordinator absorbs per-ticket
/// exceptions into <c>unresolved</c> rows internally, so the sweep
/// loop does not need try/catch around each call.
/// </summary>
public class HydrationSweeper(
    HydrationCoordinator coordinator,
    SpecificationBackfillService specBackfill,
    IHydrationTargetDatabase target,
    IOptions<HydrationOptions> options,
    ILogger logger)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public virtual Task<SpecificationBackfillResult> RunSpecificationBackfillAsync(CancellationToken ct)
        => specBackfill.RunAsync(target.DatabasePath, ct);

    public virtual async Task<PerTicketSweepResult> RunPerTicketSweepAsync(CancellationToken ct)
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<string> keys = await target.ListUnresolvedOrMissingHydrationKeysAsync(ct).ConfigureAwait(false);
        if (keys.Count == 0)
        {
            logger.LogInformation("Per-ticket hydration sweep: nothing to do (no missing or unresolved rows).");
            return PerTicketSweepResult.Empty;
        }

        int maxParallelism = Math.Max(1, options.Value.MaxParallelism);
        logger.LogInformation(
            "Per-ticket hydration sweep starting: {Eligible} tickets, MaxParallelism={MaxParallelism}.",
            keys.Count,
            maxParallelism);

        await Parallel.ForEachAsync(keys, new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism,
            CancellationToken = ct,
        }, async (key, token) =>
        {
            // The coordinator's fetch and SaveHydrationAsync are coupled inside HydrateAsync;
            // gate the entire call so writes never overlap. Throughput is preserved because
            // up to MaxParallelism concurrent HTTP fetches queue at the gate while their
            // predecessor's write commits.
            await _writeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await coordinator.HydrateAsync(key, token).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }).ConfigureAwait(false);

        sw.Stop();
        logger.LogInformation(
            "Per-ticket hydration sweep complete: {Eligible} tickets in {Elapsed}.",
            keys.Count,
            sw.Elapsed);
        return new PerTicketSweepResult(keys.Count, sw.Elapsed);
    }

    public virtual async Task<HydrationSweepResult> RunFullAsync(HydrationSweepReason reason, CancellationToken ct)
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        logger.LogInformation("Hydration sweep starting ({Reason}).", reason);

        SpecificationBackfillResult spec = await RunSpecificationBackfillAsync(ct).ConfigureAwait(false);
        if (spec.Failure is not null && reason == HydrationSweepReason.Startup)
        {
            throw new HydrationSweeperUnavailableException(spec.Failure);
        }

        PerTicketSweepResult perTicket = await RunPerTicketSweepAsync(ct).ConfigureAwait(false);
        sw.Stop();

        logger.LogInformation(
            "Hydration sweep complete ({Reason}): Specification updated={Updated}, per-ticket eligible={Eligible}, total={TotalElapsed}.",
            reason,
            spec.Updated,
            perTicket.Eligible,
            sw.Elapsed);
        return new HydrationSweepResult(spec, perTicket, sw.Elapsed);
    }
}
