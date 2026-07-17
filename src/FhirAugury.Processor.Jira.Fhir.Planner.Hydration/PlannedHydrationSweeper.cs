using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Hydration;

/// <summary>
/// Coordinates the two passes of the planner-service hydration sweep:
/// Specification backfill on <c>jira_processing_source_tickets</c>
/// followed by re-hydration of every <c>planned_tickets</c> row whose
/// <c>planned_ticket_hydration</c> row is missing or
/// <c>HydrationStatus = 'unresolved'</c>.
/// </summary>
/// <remarks>
/// Per-ticket loop calls <see cref="PlannedTicketHydrator.HydrateAsync"/>
/// directly so test subclasses of <see cref="PlannedTicketHydrator"/>
/// keep receiving every call. Same body shape as the preparer's
/// <c>PreparedHydrationSweeper</c> (intentional; the shared
/// <see cref="HydrationSweeper"/> from the common library is available
/// but the seam-preserving call into the per-service hydrator wrapper
/// keeps the test ergonomics consistent across both services).
/// </remarks>
public class PlannedHydrationSweeper(
    PlannedTicketHydrator hydrator,
    SpecificationBackfillService specBackfill,
    PlannerDatabase database,
    IOptions<HydrationOptions> options,
    ILogger<PlannedHydrationSweeper> logger)
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
            logger.LogInformation("Planner per-ticket hydration sweep: nothing to do.");
            return PerTicketSweepResult.Empty;
        }

        int maxParallelism = Math.Max(1, options.Value.MaxParallelism);
        logger.LogInformation("Planner per-ticket hydration sweep starting: {Eligible} tickets, MaxParallelism={MaxParallelism}.",
            keys.Count, maxParallelism);

        await Parallel.ForEachAsync(keys, new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism,
            CancellationToken = ct,
        }, async (key, token) =>
        {
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
        logger.LogInformation("Planner per-ticket hydration sweep complete: {Eligible} tickets in {Elapsed}.",
            keys.Count, sw.Elapsed);
        return new PerTicketSweepResult(keys.Count, sw.Elapsed);
    }

    public virtual async Task<HydrationSweepResult> RunFullAsync(HydrationSweepReason reason, CancellationToken ct)
    {
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        logger.LogInformation("Planner hydration sweep starting ({Reason}).", reason);

        SpecificationBackfillResult spec = await RunSpecificationBackfillAsync(ct).ConfigureAwait(false);
        if (spec.Failure is not null && reason == HydrationSweepReason.Startup)
        {
            throw new HydrationSweeperUnavailableException(spec.Failure);
        }

        PerTicketSweepResult perTicket = await RunPerTicketSweepAsync(ct).ConfigureAwait(false);
        sw.Stop();
        logger.LogInformation("Planner hydration sweep complete ({Reason}): Specification updated={Updated}, eligible={Eligible}, total={Total}.",
            reason, spec.Updated, perTicket.Eligible, sw.Elapsed);
        return new HydrationSweepResult(spec, perTicket, sw.Elapsed);
    }
}
