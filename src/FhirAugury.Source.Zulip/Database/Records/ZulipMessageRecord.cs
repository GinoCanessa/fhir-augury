using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Zulip.Database.Records;

/// <summary>A Zulip message within a stream and topic.</summary>
[LdgSQLiteTable("zulip_messages")]
[LdgSQLiteIndex(nameof(StreamId), nameof(Topic))]
[LdgSQLiteIndex(nameof(SenderId))]
[LdgSQLiteIndex(nameof(SenderName))]
[LdgSQLiteIndex(nameof(Timestamp))]
[LdgSQLiteIndex(nameof(StreamName), nameof(Topic))]
public partial record class ZulipMessageRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    /// <summary>The Zulip API message ID.</summary>
    [LdgSQLiteUnique]
    [LdgSQLiteMultiSelect]
    public required int ZulipMessageId { get; set; }

    [LdgSQLiteForeignKey(referenceColumn: nameof(ZulipStreamRecord.Id))]
    public required int StreamId { get; set; }

    public required string StreamName { get; set; }
    public required string Topic { get; set; }
    public required int SenderId { get; set; }
    public required string SenderName { get; set; }
    public required string? SenderEmail { get; set; }
    public required string? ContentHtml { get; set; }
    public required string ContentPlain { get; set; }
    public required DateTimeOffset Timestamp { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required string? Reactions { get; set; }
}
