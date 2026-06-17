using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;

/// <summary>
/// One drafted ballot note for a single unit (artifact, narrative page, or the
/// consolidated datatypes surface) of a FHIR GitHub repository, anchored at a
/// since-commit window. The <em>evidence</em> half (window, source files,
/// commits, tickets, current ballot-note HTML) is written by the BallotNotes
/// processor's hydration; the <em>prose</em> half (proposed note, roll-up,
/// notes-for-reviewer) is written back by the <c>notes-artifact</c> /
/// <c>notes-page</c> / <c>notes-datatype</c> skills. The child
/// <see cref="NoteSourceFileRecord"/>, <see cref="NoteCommitRecord"/>, and
/// <see cref="NoteTicketRecord"/> rows hang off <see cref="NoteId"/>.
/// </summary>
[LdgSQLiteTable("notes")]
[LdgSQLiteIndex(nameof(Type))]
[LdgSQLiteIndex(nameof(WorkGroupCode))]
[LdgSQLiteIndex(nameof(NeedsNote))]
public partial record class NoteRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    /// <summary>
    /// Deterministic business key: a slug of repo + type + unit name. Re-writing
    /// the same unit replaces the prior row (and its children).
    /// </summary>
    [LdgSQLiteUnique]
    public required string NoteId { get; set; }

    /// <summary>Unit kind: <c>Artifact</c>, <c>Page</c>, or <c>DataType</c>.</summary>
    public required string Type { get; set; }

    /// <summary>Unit name (e.g. <c>Observation</c>, <c>security</c>, <c>datatypes</c>).</summary>
    public required string Name { get; set; }

    public required string RepoOwner { get; set; }
    public required string RepoName { get; set; }
    public string RepoCategory { get; set; } = string.Empty;

    /// <summary>Owning work group display name (e.g. <c>FHIR Infrastructure (FHIR-I)</c>).</summary>
    public string WorkGroup { get; set; } = string.Empty;

    /// <summary>Owning work group code / slug used for grouping (e.g. <c>FHIR-I</c>).</summary>
    public string WorkGroupCode { get; set; } = string.Empty;

    public string SinceSha { get; set; } = string.Empty;
    public string SinceShortSha { get; set; } = string.Empty;
    public string HeadSha { get; set; } = string.Empty;
    public string HeadShortSha { get; set; } = string.Empty;

    public int CommitsInWindow { get; set; }
    public int TicketsAttributed { get; set; }

    /// <summary>Recommendation flag: <c>yes</c>, <c>no</c>, or <c>unknown</c>.</summary>
    public string NeedsNote { get; set; } = "unknown";

    /// <summary>Existing ballot note HTML at HEAD (the <c>&lt;blockquote&gt;</c> block), if any. Evidence.</summary>
    public string CurrentBallotNoteHtml { get; set; } = string.Empty;

    /// <summary>Proposed (drafted) ballot note HTML. Prose.</summary>
    public string ProposedBallotNoteHtml { get; set; } = string.Empty;

    /// <summary>After-applied roll-up summary (Markdown; rendered via marked + DOMPurify). Prose.</summary>
    public string RollupSummaryMarkdown { get; set; } = string.Empty;

    /// <summary>Notes-for-reviewer prose (Markdown; rendered via marked + DOMPurify). Prose.</summary>
    public string NotesForReviewerMarkdown { get; set; } = string.Empty;

    /// <summary>Optional free-text note about source-file patterns that produced no match. Prose.</summary>
    public string SourceFilesNote { get; set; } = string.Empty;

    /// <summary>When the unit's evidence was last hydrated by the processor.</summary>
    public DateTimeOffset? HydratedAt { get; set; }

    /// <summary>When prose was last authored back for the unit; <c>null</c> while awaiting a note.</summary>
    public DateTimeOffset? AuthoredAt { get; set; }

    /// <summary>
    /// Display timestamp the report SPA shows ("Generated"). Set on hydration and
    /// refreshed when prose is authored. Preserved for SPA compatibility.
    /// </summary>
    public required DateTimeOffset GeneratedAt { get; set; }

    /// <summary>Last-touch timestamp (evidence upsert or prose write).</summary>
    public required DateTimeOffset SavedAt { get; set; }
}
