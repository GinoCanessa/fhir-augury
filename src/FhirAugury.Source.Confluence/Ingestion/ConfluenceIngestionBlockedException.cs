using FhirAugury.Source.Confluence.Database.Records;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>
/// Refuses a network run because ingestion is already blocked and a human has
/// not cleared it yet.
/// </summary>
/// <remarks>
/// The sibling <see cref="ConfluenceHumanInterventionRequiredException"/> means
/// "we just hit the wall"; this one means "we already know about the wall".
/// Derived from <see cref="Exception"/> rather than
/// <see cref="InvalidOperationException"/> on purpose:
/// <c>IngestionController</c> already maps that to <c>409 Conflict</c> for a run
/// that is in progress, and a blocked run has to surface as <c>412</c>.
/// </remarks>
public sealed class ConfluenceIngestionBlockedException(ConfluenceIngestionBlockRecord block)
    : Exception(
        $"Confluence ingestion is blocked ({block.Fingerprint ?? "edge challenge"}) since {block.BlockedAt:u}. " +
        $"{ConfluenceHumanInterventionRequiredException.RemediationText}")
{
    /// <summary>The durable block row that caused the refusal.</summary>
    public ConfluenceIngestionBlockRecord Block { get; } = block;
}
