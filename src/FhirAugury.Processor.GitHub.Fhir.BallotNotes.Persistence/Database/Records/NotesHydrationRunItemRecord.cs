using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;

[LdgSQLiteTable("notes_hydration_run_items")]
[LdgSQLiteIndex(nameof(ExecutionId))]
[LdgSQLiteIndex(nameof(NoteId))]
[LdgSQLiteIndex(nameof(Status))]
[LdgSQLiteIndex(nameof(EvidenceRevision))]
public partial record class NotesHydrationRunItemRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string Id { get; set; }

    public required string ExecutionId { get; set; }
    public required string NoteId { get; set; }
    public required string Type { get; set; }
    public int ItemOrder { get; set; }
    public required string Status { get; set; }
    public string EvidenceHash { get; set; } = string.Empty;
    public string EvidenceRevision { get; set; } = string.Empty;
    public DateTimeOffset? HydratedAt { get; set; }
    public string Error { get; set; } = string.Empty;
}
