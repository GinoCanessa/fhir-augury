using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;

/// <summary>
/// One structural delta to a StructureDefinition element detected over the
/// since-commit window (element added/removed, or a change to cardinality,
/// type, is-modifier, is-summary, or must-support). Child of
/// <see cref="NoteRecord"/> via <see cref="NoteId"/>. Drives the SPA's
/// "Structural changes" evidence panel and the inline structural badges the
/// authoring skills embed.
/// </summary>
[LdgSQLiteTable("note_structural_changes")]
[LdgSQLiteIndex(nameof(NoteId))]
public partial record class NoteStructuralChangeRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string NoteId { get; set; }

    /// <summary>The StructureDefinition source file the delta was found in.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>The element path the delta applies to (e.g. <c>Observation.value[x]</c>).</summary>
    public string ElementPath { get; set; } = string.Empty;

    /// <summary>Delta kind: <c>Added</c>, <c>Removed</c>, <c>Cardinality</c>, <c>Type</c>, <c>Modifier</c>, <c>Summary</c>, or <c>MustSupport</c>.</summary>
    public string ChangeKind { get; set; } = string.Empty;

    /// <summary>Human-readable delta string (e.g. <c>min 0→1</c>).</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>File-level attributed ticket keys (semicolon-joined); empty when none.</summary>
    public string TicketKeys { get; set; } = string.Empty;

    public int ChangeOrder { get; set; }
}
