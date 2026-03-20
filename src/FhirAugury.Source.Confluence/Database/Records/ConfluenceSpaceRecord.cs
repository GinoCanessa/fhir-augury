using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Confluence.Database.Records;

/// <summary>A Confluence space with metadata.</summary>
[LdgSQLiteTable("confluence_spaces")]
[LdgSQLiteIndex(nameof(Key))]
public partial record class ConfluenceSpaceRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string Key { get; set; }

    public required string Name { get; set; }
    public required string? Description { get; set; }
    public required string? Url { get; set; }
    public required DateTimeOffset LastFetchedAt { get; set; }
}
