using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;

/// <summary>
/// Resolves the work group(s) that <em>own</em> a hydration unit, independent of
/// which work group raised the attributed tickets. The owner is determined by a
/// deterministic chain that varies by unit type:
/// <list type="bullet">
///   <item><c>Artifact</c>: registry → repo-read → spec-DB → base-resource →
///   most-recent ticket → <c>(unknown)</c>.</item>
///   <item><c>Page</c>: registry → page marker → <c>(unknown)</c> (never ticket).</item>
///   <item><c>DataType</c>: the distinct set of owners of the covered datatypes
///   (each via the artifact own-WG chain; never ticket).</item>
/// </list>
/// This is the seam introduced in Phase 1; only the ticket step is wired here and
/// later phases layer the registry / repo-read / spec-DB / base-resource /
/// datatype sources in front of it. The primary owner is always the first entry
/// of the returned list.
/// </summary>
public static class OwningWorkGroupResolver
{
    /// <summary>
    /// Resolves the owning work group set for <paramref name="unit"/>. Phase 1
    /// reproduces the legacy ticket-recency owner exactly (a single-element list
    /// derived from <see cref="TicketAttributor.SelectOwningWorkGroup"/>).
    /// </summary>
    public static IReadOnlyList<WorkGroupRef> Resolve(
        HydrationUnit unit,
        string clonePath,
        string owner,
        string name,
        UnitAttribution attribution,
        string? workGroupHint,
        BallotNotesHydrationOptions options,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(attribution);

        (string workGroup, string workGroupCode) =
            TicketAttributor.SelectOwningWorkGroup(attribution.Tickets, workGroupHint);

        // Phase 1: ticket-only. A single ref preserves the prior empty-string
        // output exactly when no ticket / hint carries a work group.
        return [new WorkGroupRef(workGroupCode, workGroup)];
    }
}
