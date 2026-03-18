using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Database.Records;

/// <summary>A comment on a Confluence page.</summary>
[LdgSQLiteTable("confluence_comments")]
[LdgSQLiteIndex(nameof(PageId))]
public partial record class ConfluenceCommentRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteForeignKey(referenceColumn: nameof(ConfluencePageRecord.Id))]
    public required int PageId { get; set; }

    public required string ConfluencePageId { get; set; }
    public required string Author { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required string Body { get; set; }
}
