using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Tools.NotesSite.Database.Records;

/// <summary>
/// A commit that touched the note's unit within the since-commit window, with
/// the Jira ticket keys attributed to it. Child of <see cref="NoteRecord"/> via
/// <see cref="NoteId"/>.
/// </summary>
[LdgSQLiteTable("note_commits")]
[LdgSQLiteIndex(nameof(NoteId))]
public partial record class NoteCommitRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string NoteId { get; set; }

    public required string Sha { get; set; }
    public string ShortSha { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;

    /// <summary>ISO-8601 author date, kept as text for display fidelity.</summary>
    public string AuthorDate { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;
    public string WebUrl { get; set; } = string.Empty;

    /// <summary>Comma-separated attributed ticket keys (e.g. <c>FHIR-12345, FHIR-23456</c>).</summary>
    public string TicketKeys { get; set; } = string.Empty;

    public int CommitOrder { get; set; }
}
