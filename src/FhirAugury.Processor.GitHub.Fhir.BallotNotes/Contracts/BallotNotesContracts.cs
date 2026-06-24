using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Models;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;

/// <summary>Request to hydrate a repo + since-commit window.</summary>
public sealed record HydrateRequest
{
    public required string RepoOwner { get; init; }
    public required string RepoName { get; init; }
    public required string SinceSha { get; init; }
    public string? RepoCategory { get; init; }
    public string? WorkGroupHint { get; init; }

    /// <summary>Human-readable window label (e.g. <c>R6 Ballot 4</c>) shown in the note + SPA.</summary>
    public string? WindowLabel { get; init; }
}

/// <summary>Returned with <c>202 Accepted</c> when a hydration run is queued.</summary>
public sealed record HydrateAcceptedDto
{
    public required string RunKey { get; init; }
    public required string Status { get; init; }
    public int UnitsTotal { get; init; }
}

/// <summary>Pollable status of a hydration run.</summary>
public sealed record HydrationStatusDto
{
    public required string RunKey { get; init; }
    public required string Status { get; init; }
    public int UnitsTotal { get; init; }
    public int UnitsHydrated { get; init; }
    public int CommitsInWindow { get; init; }
    public int TicketsAttributed { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string Error { get; init; } = string.Empty;
}

/// <summary>A page of listed notes.</summary>
public sealed record BallotNoteListResponse
{
    public required int Total { get; init; }
    public required IReadOnlyList<NoteListRow> Notes { get; init; }
}

/// <summary>A source file belonging to a note's unit.</summary>
public sealed record NoteSourceFileDto
{
    public required string Path { get; init; }
    public string Role { get; init; } = string.Empty;
    public bool TouchedInWindow { get; init; }
}

/// <summary>A window commit with its attributed ticket keys.</summary>
public sealed record NoteCommitDto
{
    public required string Sha { get; init; }
    public string ShortSha { get; init; } = string.Empty;
    public string AuthorName { get; init; } = string.Empty;
    public string AuthorDate { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string WebUrl { get; init; } = string.Empty;
    public string TicketKeys { get; init; } = string.Empty;
}

/// <summary>A Jira ticket attributed to the note's unit.</summary>
public sealed record NoteTicketDto
{
    public required string TicketKey { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Resolution { get; init; } = string.Empty;
    public string WorkGroup { get; init; } = string.Empty;
    public string Specification { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public int CommitCount { get; init; }

    /// <summary>The ticket's Jira change-impact classification; empty when unset.</summary>
    public string ChangeImpact { get; init; } = string.Empty;

    /// <summary>The ticket's Jira change-category classification; empty when unset.</summary>
    public string ChangeCategory { get; init; } = string.Empty;

    /// <summary>Related/linked Jira ticket keys gathered from the issue's links; empty when none.</summary>
    public IReadOnlyList<string> RelatedTicketKeys { get; init; } = [];
}

/// <summary>One note with its full hydrated evidence and any authored prose.</summary>
public sealed record BallotNoteDetailDto
{
    public required string NoteId { get; init; }
    public required string Type { get; init; }
    public required string Name { get; init; }
    public required string Status { get; init; }

    public string RepoOwner { get; init; } = string.Empty;
    public string RepoName { get; init; } = string.Empty;
    public string RepoCategory { get; init; } = string.Empty;
    public string WorkGroup { get; init; } = string.Empty;
    public string WorkGroupCode { get; init; } = string.Empty;

    public string SinceSha { get; init; } = string.Empty;
    public string SinceShortSha { get; init; } = string.Empty;
    public string HeadSha { get; init; } = string.Empty;
    public string HeadShortSha { get; init; } = string.Empty;

    /// <summary>Human-readable window label (e.g. <c>R6 Ballot 4</c>); empty when not supplied.</summary>
    public string WindowLabel { get; init; } = string.Empty;

    public int CommitsInWindow { get; init; }
    public int TicketsAttributed { get; init; }

    public string NeedsNote { get; init; } = "unknown";
    public string CurrentBallotNoteHtml { get; init; } = string.Empty;

    /// <summary>Whether the current note at HEAD is tool-generated (carries the augury marker).</summary>
    public bool CurrentNoteIsAuguryGenerated { get; init; }

    /// <summary>Hand-authored note blocks at HEAD to carry forward verbatim alongside a regenerated note.</summary>
    public string PreservedHandAuthoredHtml { get; init; } = string.Empty;

    public string ProposedBallotNoteHtml { get; init; } = string.Empty;
    public string RollupSummaryMarkdown { get; init; } = string.Empty;
    public string NotesForReviewerMarkdown { get; init; } = string.Empty;
    public string SourceFilesNote { get; init; } = string.Empty;

    public DateTimeOffset? HydratedAt { get; init; }
    public DateTimeOffset? AuthoredAt { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }

    public required IReadOnlyList<NoteSourceFileDto> SourceFiles { get; init; }
    public required IReadOnlyList<NoteCommitDto> Commits { get; init; }
    public required IReadOnlyList<NoteTicketDto> Tickets { get; init; }
}

/// <summary>The authored prose written back for a unit.</summary>
public sealed record BallotNoteProsePutRequest
{
    public string NeedsNote { get; init; } = "unknown";
    public string ProposedBallotNoteHtml { get; init; } = string.Empty;
    public string RollupSummaryMarkdown { get; init; } = string.Empty;
    public string NotesForReviewerMarkdown { get; init; } = string.Empty;
    public string SourceFilesNote { get; init; } = string.Empty;
}

/// <summary>Result of a prose write-back.</summary>
public sealed record BallotNoteProseSaveResultDto
{
    public required string NoteId { get; init; }
    public required string Status { get; init; }
}
