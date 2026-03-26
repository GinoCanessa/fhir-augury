using CsLightDbGen.SQLiteGenerator;
using FhirAugury.Common.Database;

namespace FhirAugury.Common.Database.Records;

/// <summary>A Confluence page reference extracted from text in any source.</summary>
[LdgSQLiteTable("xref_confluence")]
[LdgSQLiteIndex(nameof(SourceType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(PageId))]
public partial record class ConfluenceXRefRecord : ICrossReferenceRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }
    public required string SourceType { get; set; }
    public required string SourceId { get; set; }
    public required string LinkType { get; set; }
    public required string? Context { get; set; }
    public required string PageId { get; set; }

    [LdgSQLiteIgnore] public string TargetType => "confluence";
    [LdgSQLiteIgnore] public string TargetId => PageId;
}
