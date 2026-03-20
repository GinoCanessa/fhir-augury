using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Confluence.Database.Records;

/// <summary>An internal link between two Confluence pages.</summary>
[LdgSQLiteTable("confluence_page_links")]
[LdgSQLiteIndex(nameof(SourcePageId))]
[LdgSQLiteIndex(nameof(TargetPageId))]
[LdgSQLiteIndex(nameof(LinkType))]
public partial record class ConfluencePageLinkRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string SourcePageId { get; set; }
    public required string TargetPageId { get; set; }
    public required string LinkType { get; set; }
}
