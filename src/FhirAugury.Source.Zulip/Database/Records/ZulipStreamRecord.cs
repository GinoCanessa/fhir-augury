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

    /// <summary>
    /// Baseline ranking value for this stream (0–10, default 5).
    /// Scores are multiplied by BaselineValue / 5.0 so that default is neutral.
    /// Use lower values for noisy streams (e.g. build notifications) and higher
    /// values for high-signal discussion streams.
    /// </summary>
    public required int BaselineValue { get; set; } = 5;

    public required DateTimeOffset LastFetchedAt { get; set; }
}
