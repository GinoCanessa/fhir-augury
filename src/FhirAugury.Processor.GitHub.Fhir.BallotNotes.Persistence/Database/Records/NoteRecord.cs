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

    /// <summary>
    /// Distinct, semicolon-delimited set of owning work group display names. A
    /// single value for single-owner units (artifacts / pages); many for the
    /// consolidated datatypes surface, which may belong to several work groups.
    /// <see cref="WorkGroup"/> is the deterministic primary owner and the first
    /// entry of this set.
    /// </summary>
    public string WorkGroupNames { get; set; } = string.Empty;

    /// <summary>
    /// Distinct, semicolon-delimited set of owning work group canonical codes,
    /// index-aligned with <see cref="WorkGroupNames"/>. Preserves WG-code
    /// filtering for secondary memberships; <see cref="WorkGroupCode"/> is the
    /// primary and the first entry of this set.
    /// </summary>
    public string WorkGroupCodes { get; set; } = string.Empty;

    /// <summary>
    /// <em>Listed</em> work group display names — the WG declared on the
    /// artifact/page definition itself, read from the repo clone
    /// (StructureDefinition <c>structuredefinition-wg</c> for artifacts; the page
    /// <c>[%wg%]</c> "Responsible Owner" marker for pages). A single value for
    /// artifacts/pages; a per-covered-datatype set for the consolidated datatypes
    /// surface. Authoritative source is the repo-read, never the JIRA index: when
    /// an artifact/page declares no WG the value is <c>(unknown)</c> / empty rather
    /// than borrowing the index — <em>except</em> the datatypes surface, whose
    /// Listed falls back to FHIR Infrastructure per covered datatype when repo-read
    /// is empty. Index-aligned with <see cref="ListedWorkGroupCodes"/>.
    /// </summary>
    public string ListedWorkGroupNames { get; set; } = string.Empty;

    /// <summary>
    /// Distinct, semicolon-delimited set of <em>Listed</em> work group canonical
    /// codes, index-aligned with <see cref="ListedWorkGroupNames"/>. See that
    /// property for the repo-read sourcing rules.
    /// </summary>
    public string ListedWorkGroupCodes { get; set; } = string.Empty;

    /// <summary>
    /// <em>JIRA index</em> work group display names — the WG from the JIRA
    /// spec-artifact/page registry (<c>jira_spec_artifacts</c> /
    /// <c>jira_spec_pages</c>, resolved through <c>jira_workgroups</c> →
    /// <c>hl7_workgroups</c>). A single value for artifacts/pages; a
    /// per-covered-datatype set for the datatypes surface. Empty when the registry
    /// has no (unambiguous) owner. Index-aligned with
    /// <see cref="IndexWorkGroupCodes"/>.
    /// </summary>
    public string IndexWorkGroupNames { get; set; } = string.Empty;

    /// <summary>
    /// Distinct, semicolon-delimited set of <em>JIRA index</em> work group
    /// canonical codes, index-aligned with <see cref="IndexWorkGroupNames"/>.
    /// </summary>
    public string IndexWorkGroupCodes { get; set; } = string.Empty;

    /// <summary>
    /// <em>Applied-by</em> work group display names — the distinct set of work
    /// groups whose attributed tickets produced an in-window commit touching the
    /// unit's source files. Surfaces who actually moved the artifact during the
    /// window, independent of who owns it. Falls back to all attributed tickets'
    /// work groups (see <see cref="SourceFilesNote"/> for the imprecision warning)
    /// when commit-to-file granularity is unavailable. Index-aligned with
    /// <see cref="AppliedWorkGroupCodes"/>.
    /// </summary>
    public string AppliedWorkGroupNames { get; set; } = string.Empty;

    /// <summary>
    /// Distinct, semicolon-delimited set of <em>Applied-by</em> work group
    /// canonical codes, index-aligned with <see cref="AppliedWorkGroupNames"/>.
    /// Codes derive from <c>Hl7WorkGroupNameCleaner.Clean</c> of each ticket's
    /// work-group name — the same canonical basis as Listed/Index codes.
    /// </summary>
    public string AppliedWorkGroupCodes { get; set; } = string.Empty;

    public string SinceSha { get; set; } = string.Empty;
    public string SinceShortSha { get; set; } = string.Empty;
    public string HeadSha { get; set; } = string.Empty;
    public string HeadShortSha { get; set; } = string.Empty;

    /// <summary>Human-readable window label (e.g. <c>R6 Ballot 4</c>); empty when not supplied.</summary>
    public string WindowLabel { get; set; } = string.Empty;

    public int CommitsInWindow { get; set; }
    public int TicketsAttributed { get; set; }

    /// <summary>Recommendation flag: <c>yes</c>, <c>no</c>, or <c>unknown</c>.</summary>
    public string NeedsNote { get; set; } = "unknown";

    /// <summary>Existing ballot note HTML at HEAD (the <c>&lt;blockquote&gt;</c> block), if any. Evidence.</summary>
    public string CurrentBallotNoteHtml { get; set; } = string.Empty;

    /// <summary>
    /// Whether <see cref="CurrentBallotNoteHtml"/> was tool-generated (carries the
    /// <c>data-augury-generated</c> marker). When true a regenerated note replaces
    /// it; when false the current note is hand-authored. Evidence.
    /// </summary>
    public bool CurrentNoteIsAuguryGenerated { get; set; }

    /// <summary>
    /// Concatenation of the hand-authored (non-augury-generated) note blocks found
    /// at HEAD, to be carried forward verbatim alongside a regenerated note. Evidence.
    /// </summary>
    public string PreservedHandAuthoredHtml { get; set; } = string.Empty;

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
