using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;

/// <summary>
/// A source file considered part of a note's unit, with its role and whether it
/// was touched in the commit window. Child of <see cref="NoteRecord"/> via
/// <see cref="NoteId"/>.
/// </summary>
[LdgSQLiteTable("note_source_files")]
[LdgSQLiteIndex(nameof(NoteId))]
public partial record class NoteSourceFileRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string NoteId { get; set; }

    /// <summary>Clone-root-relative path (e.g. <c>source/observation/observation-introduction.xml</c>).</summary>
    public required string Path { get; set; }

    /// <summary>One-line role description (e.g. "StructureDefinition", "Narrative intro").</summary>
    public string Role { get; set; } = string.Empty;

    public bool TouchedInWindow { get; set; }

    public int FileOrder { get; set; }
}
