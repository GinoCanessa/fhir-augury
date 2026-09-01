using System.Collections.Concurrent;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration;

/// <summary>
/// Per-run shared, thread-safe state handed to the parallel unit workers so that
/// repeated git and network work is performed once per hydration run instead of
/// once per unit. Constructed a single time in
/// <see cref="BallotNotesHydrator.HydrateAsync"/> and treated as read-only by the
/// workers unless a member is explicitly documented as a concurrent memo.
/// </summary>
public sealed class HydrationRunContext
{
    /// <summary>
    /// Every unit's candidate current-note intro file, read at HEAD in one
    /// <c>git cat-file --batch</c> pass and keyed by the <c>HEAD:&lt;path&gt;</c>
    /// spec used to request it. A missing candidate is present with
    /// <see cref="BlobResult.Found"/> = <c>false</c>.
    /// </summary>
    public required IReadOnlyDictionary<string, BlobResult> CurrentNoteBlobs { get; init; }

    /// <summary>
    /// Run-scoped cross-reference memo: full commit SHA (ordinal) → the ticket keys
    /// its cross-referenced lookup yields, computed once per run and shared across
    /// every unit whose window contains that commit. The <see cref="Lazy{T}"/> uses
    /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> to coalesce
    /// concurrent duplicate lookups across the parallel unit fan-out.
    /// </summary>
    internal ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<string>>>> CrossReferenceMemo { get; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Run-scoped ticket-detail memo: upper-cased ticket key (ordinal) → the
    /// best-effort enrichment fetched once per run and shared across every unit that
    /// attributes that ticket. Same <see cref="Lazy{T}"/> coalescing semantics as
    /// <see cref="CrossReferenceMemo"/>.
    /// </summary>
    internal ConcurrentDictionary<string, Lazy<Task<TicketAttributor.TicketDetails>>> TicketDetailsMemo { get; } =
        new(StringComparer.Ordinal);
}
