using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Database.Records;

/// <summary>A Confluence space containing pages.</summary>
[LdgSQLiteTable("confluence_spaces")]
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
