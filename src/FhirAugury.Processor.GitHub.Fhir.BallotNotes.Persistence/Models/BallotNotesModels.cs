using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Models;

/// <summary>
/// The authored prose half of a ballot note, written back by the
/// <c>notes-artifact</c> / <c>notes-page</c> / <c>notes-datatype</c> skills. The
/// evidence half (window, source files, commits, tickets, current HTML) is owned
/// by hydration and never supplied here.
/// </summary>
public sealed record BallotNoteProse
{
    /// <summary>Recommendation flag: <c>yes</c>, <c>no</c>, or <c>unknown</c>.</summary>
    public string NeedsNote { get; init; } = "unknown";

    public string ProposedBallotNoteHtml { get; init; } = string.Empty;
    public string RollupSummaryMarkdown { get; init; } = string.Empty;
    public string NotesForReviewerMarkdown { get; init; } = string.Empty;
    public string SourceFilesNote { get; init; } = string.Empty;
}

/// <summary>Query filter for <see cref="Database.BallotNotesDatabase.ListNotes"/>.</summary>
public sealed record NoteQueryFilter
{
    /// <summary>Repository as <c>owner/name</c> (e.g. <c>HL7/fhir</c>); <c>null</c> for any.</summary>
    public string? Repo { get; init; }

    /// <summary>Owning work group code / slug (e.g. <c>FHIR-I</c>); <c>null</c> for any.</summary>
    public string? WorkGroupCode { get; init; }

    /// <summary>Unit kind (<c>Artifact</c>, <c>Page</c>, <c>DataType</c>); <c>null</c> for any.</summary>
    public string? Type { get; init; }

    /// <summary>Recommendation flag (<c>yes</c>, <c>no</c>, <c>unknown</c>); <c>null</c> for any.</summary>
    public string? NeedsNote { get; init; }

    /// <summary>Authoring status (<c>authored</c> or <c>awaiting-note</c>); <c>null</c> for any.</summary>
    public string? Status { get; init; }

    public int Limit { get; init; } = 50;
    public int Offset { get; init; }
}

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

    /// <summary><c>authored</c> when prose has been written, else <c>awaiting-note</c>.</summary>
    public required string Status { get; init; }

    public DateTimeOffset? HydratedAt { get; init; }
    public DateTimeOffset? AuthoredAt { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>A note with its full hydrated evidence and any authored prose.</summary>
public sealed record NoteDetail
{
    public required NoteRecord Note { get; init; }
    public required IReadOnlyList<NoteSourceFileRecord> SourceFiles { get; init; }
    public required IReadOnlyList<NoteCommitRecord> Commits { get; init; }
    public required IReadOnlyList<NoteTicketRecord> Tickets { get; init; }

    /// <summary><c>authored</c> when <see cref="NoteRecord.AuthoredAt"/> is set, else <c>awaiting-note</c>.</summary>
    public string Status => Note.AuthoredAt is not null ? "authored" : "awaiting-note";
}
