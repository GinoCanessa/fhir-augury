using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Hydration;

/// <summary>
/// Why a hydration sweep is being run. Affects how an unavailable
/// Specification upstream is surfaced: a <see cref="Startup"/> sweep
/// throws to abort host startup; an <see cref="AdminRequest"/> sweep
/// returns the failure as a result so the controller can map it to a
/// 503 response without affecting service liveness.
/// </summary>
public enum HydrationSweepReason
{
    Startup,
    AdminRequest,
}

/// <summary>
/// Result of a per-ticket hydration sweep pass. <see cref="Eligible"/>
/// is the number of <c>prepared_tickets</c> rows whose hydration row
/// was missing or unresolved at the start of the sweep — i.e., the
/// number of <see cref="PreparedTicketHydrator.HydrateAsync"/> calls
/// the sweep issued.
/// </summary>
public sealed record PerTicketSweepResult(int Eligible, TimeSpan Elapsed)
{
    public static PerTicketSweepResult Empty { get; } = new(0, TimeSpan.Zero);
}

/// <summary>
/// Composite result of a <see cref="PreparedHydrationSweeper.RunFullAsync"/>
/// call.
/// </summary>
public sealed record HydrationSweepResult(
    SpecificationBackfillResult Specification,
    PerTicketSweepResult PerTicket,
    TimeSpan TotalElapsed);

/// <summary>
/// Thrown by <see cref="PreparedHydrationSweeper.RunFullAsync"/> when
/// invoked with <see cref="HydrationSweepReason.Startup"/> and the
/// Specification backfill upstream is unreachable. Aborting host
/// startup is the documented hard-fail behavior.
/// </summary>
public sealed class HydrationSweeperUnavailableException(SpecificationBackfillFailure failure)
    : Exception(failure.Reason)
{
    public SpecificationBackfillFailure Failure { get; } = failure;
}

/// <summary>
/// Coordinates the two passes of the preparer-service hydration sweep:
/// Specification backfill on <c>jira_processing_source_tickets</c>
/// followed by re-hydration of every <c>prepared_tickets</c> row whose
/// <c>prepared_ticket_hydration</c> row is missing or
/// <c>HydrationStatus = 'unresolved'</c>. Used by the startup hosted
/// service and the admin endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Per-ticket fan-out is bounded by
/// <see cref="HydrationOptions.MaxParallelism"/> and writes are
/// serialized through a private semaphore so concurrent
/// <see cref="PreparedTicketHydrator.HydrateAsync"/> calls cannot
/// collide on the SQLite WAL writer. The hydrator absorbs per-ticket
/// exceptions into <c>unresolved</c> rows internally, so the sweep
/// loop does not need try/catch around each call.
/// </para>
/// </remarks>
public class PreparedHydrationSweeper(
    PreparedTicketHydrator hydrator,
    SpecificationBackfillService specBackfill,
    PreparerDatabase database,
    IOptions<HydrationOptions> options,
    ILogger<PreparedHydrationSweeper> logger)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public virtual Task<SpecificationBackfillResult> RunSpecificationBackfillAsync(CancellationToken ct)
        => specBackfill.RunAsync(database.DatabasePath, ct);

    public virtual async Task<PerTicketSweepResult> RunPerTicketSweepAsync(CancellationToken ct)
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<string> keys = await database.ListUnresolvedOrMissingHydrationKeysAsync(ct).ConfigureAwait(false);
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
            // The hydrator's fetch and SaveHydrationAsync are coupled inside HydrateAsync;
            // gate the entire call so writes never overlap. Throughput is preserved because
            // up to MaxParallelism concurrent HTTP fetches queue at the gate while their
            // predecessor's write commits.
            await _writeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await hydrator.HydrateAsync(key, token).ConfigureAwait(false);
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
