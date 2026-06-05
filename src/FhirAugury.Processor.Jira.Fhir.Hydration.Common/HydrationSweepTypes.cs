namespace FhirAugury.Processor.Jira.Fhir.Hydration.Common;

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
/// is the number of rows whose hydration row was missing or unresolved
/// at the start of the sweep — i.e., the number of
/// <see cref="HydrationCoordinator.HydrateAsync"/> calls the sweep
/// issued.
/// </summary>
public sealed record PerTicketSweepResult(int Eligible, TimeSpan Elapsed)
{
    public static PerTicketSweepResult Empty { get; } = new(0, TimeSpan.Zero);
}

/// <summary>
/// Composite result of a <see cref="HydrationSweeper.RunFullAsync"/>
/// call.
/// </summary>
public sealed record HydrationSweepResult(
    SpecificationBackfillResult Specification,
    PerTicketSweepResult PerTicket,
    TimeSpan TotalElapsed);

/// <summary>
/// Thrown by <see cref="HydrationSweeper.RunFullAsync"/> when invoked
/// with <see cref="HydrationSweepReason.Startup"/> and the Specification
/// backfill upstream is unreachable. Aborting host startup is the
/// documented hard-fail behavior.
/// </summary>
public sealed class HydrationSweeperUnavailableException(SpecificationBackfillFailure failure)
    : Exception(failure.Reason)
{
    public SpecificationBackfillFailure Failure { get; } = failure;
}
