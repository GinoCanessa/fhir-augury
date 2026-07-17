using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;

/// <summary>
/// A referenced extension (from the <c>HL7/fhir-extensions</c> pack) that the
/// CI build maps to a replacing core element, surfaced on the note with a
/// rationale. Child of <see cref="NoteRecord"/> via <see cref="NoteId"/>.
/// Extension-only churn with no core counterpart is suppressed and never
/// persisted here.
/// </summary>
[LdgSQLiteTable("note_extension_refs")]
[LdgSQLiteIndex(nameof(NoteId))]
public partial record class NoteExtensionRefRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string NoteId { get; set; }

    /// <summary>Canonical URL of the referenced extension.</summary>
    public string ExtensionUrl { get; set; } = string.Empty;

    /// <summary>Computer-friendly name of the extension.</summary>
    public string ExtensionName { get; set; } = string.Empty;

    /// <summary>The core element that replaces the extension (e.g. <c>Patient.gender</c>).</summary>
    public string ReplacementCoreElement { get; set; } = string.Empty;

    /// <summary>Human-readable rationale (the extension's description).</summary>
    public string Rationale { get; set; } = string.Empty;

    public int RefOrder { get; set; }
}
