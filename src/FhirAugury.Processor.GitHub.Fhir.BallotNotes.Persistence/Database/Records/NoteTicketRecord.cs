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

    /// <summary>The ticket's Jira change-impact classification (e.g. <c>Non-substantive</c>); empty when unset.</summary>
    public string ChangeImpact { get; set; } = string.Empty;

    /// <summary>The ticket's Jira change-category classification; empty when unset.</summary>
    public string ChangeCategory { get; set; } = string.Empty;

    /// <summary>The ticket's Jira issue Type, e.g. <c>Technical Correction</c>; empty when unset.</summary>
    public string IssueType { get; set; } = string.Empty;

    /// <summary>Related/linked Jira ticket keys (semicolon-joined, self excluded); empty when none.</summary>
    public string RelatedTicketKeys { get; set; } = string.Empty;

    /// <summary>Number of commits in the window attributed to this ticket.</summary>
    public int CommitCount { get; set; }

    public int TicketOrder { get; set; }
}
