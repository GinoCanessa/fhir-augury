using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;

/// <summary>
/// A Jira ticket attributed to the note's unit within the window. Child of
/// <see cref="NoteRecord"/> via <see cref="NoteId"/>.
/// </summary>
[LdgSQLiteTable("note_tickets")]
[LdgSQLiteIndex(nameof(NoteId))]
public partial record class NoteTicketRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string NoteId { get; set; }

    public required string TicketKey { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public string WorkGroup { get; set; } = string.Empty;
    public string Specification { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    /// <summary>Number of commits in the window attributed to this ticket.</summary>
    public int CommitCount { get; set; }

    public int TicketOrder { get; set; }
}
