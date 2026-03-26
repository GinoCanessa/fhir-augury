using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Zulip.Database.Records;

/// <summary>A Zulip stream (channel) containing messages organized by topic.</summary>
[LdgSQLiteTable("zulip_streams")]
[LdgSQLiteIndex(nameof(Name))]
public partial record class ZulipStreamRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    /// <summary>The Zulip API stream ID.</summary>
    [LdgSQLiteUnique]
    public required int ZulipStreamId { get; set; }

    public required string Name { get; set; }
    public required string? Description { get; set; }
    public required bool IsWebPublic { get; set; }
    public required int MessageCount { get; set; }
    public required bool IncludeStream { get; set; } = true;
    public required DateTimeOffset LastFetchedAt { get; set; }
}
