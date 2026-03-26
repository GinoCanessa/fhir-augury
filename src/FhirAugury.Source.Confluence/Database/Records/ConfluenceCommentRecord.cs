using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Confluence.Database.Records;

/// <summary>A comment on a Confluence page.</summary>
[LdgSQLiteTable("confluence_comments")]
[LdgSQLiteIndex(nameof(PageId))]
[LdgSQLiteIndex(nameof(ConfluencePageId))]
[LdgSQLiteIndex(nameof(CreatedAt))]
public partial record class ConfluenceCommentRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required int PageId { get; set; }
    public required string ConfluencePageId { get; set; }
    public required string Author { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required string? Body { get; set; }
}
