using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Confluence.Database.Records;

/// <summary>A Confluence page with content in both storage and plain text formats.</summary>
[LdgSQLiteTable("confluence_pages")]
[LdgSQLiteIndex(nameof(SpaceKey))]
[LdgSQLiteIndex(nameof(ParentId))]
[LdgSQLiteIndex(nameof(LastModifiedAt))]
public partial record class ConfluencePageRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string ConfluenceId { get; set; }

    public required string SpaceKey { get; set; }
    public required string Title { get; set; }
    public required string? ParentId { get; set; }
    public required string? BodyStorage { get; set; }
    public required string? BodyPlain { get; set; }
    public required string? Labels { get; set; }
    public required int VersionNumber { get; set; }
    public required string? LastModifiedBy { get; set; }
    public required DateTimeOffset LastModifiedAt { get; set; }
    public required string? Url { get; set; }
}
