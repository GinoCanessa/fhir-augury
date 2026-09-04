namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Models;

/// <summary>A single row in a notes listing.</summary>
public sealed record NoteListRow
{
    public required string NoteId { get; init; }
    public required string Type { get; init; }
    public required string Name { get; init; }
    public required string RepoOwner { get; init; }
    public required string RepoName { get; init; }
    public required string WorkGroup { get; init; }
    public required string WorkGroupCode { get; init; }
    public required string NeedsNote { get; init; }
    public int CommitsInWindow { get; init; }
    public int TicketsAttributed { get; init; }

    /// <summary>
    /// <c>authored</c> only for current-evidence receipt-backed prose; otherwise
    /// <c>stale</c>, <c>legacy-unverified</c>, or <c>awaiting-note</c>.
    /// </summary>
    public required string Status { get; init; }

    public string CurrentHydrationExecutionId { get; init; } = string.Empty;
    public string CurrentEvidenceRevision { get; init; } = string.Empty;
    public string ProseVerificationStatus { get; init; } = "unverified";
    public DateTimeOffset? HydratedAt { get; init; }
    public DateTimeOffset? AuthoredAt { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
}
