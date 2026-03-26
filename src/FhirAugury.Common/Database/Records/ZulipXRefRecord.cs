using CsLightDbGen.SQLiteGenerator;
using FhirAugury.Common.Database;

namespace FhirAugury.Common.Database.Records;

/// <summary>A Zulip chat reference extracted from text in any source.</summary>
[LdgSQLiteTable("xref_zulip")]
[LdgSQLiteIndex(nameof(SourceType), nameof(SourceId))]
[LdgSQLiteIndex(nameof(StreamId))]
[LdgSQLiteIndex(nameof(StreamId), nameof(TopicName))]
public partial record class ZulipXRefRecord : ICrossReferenceRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }
    public required string SourceType { get; set; }
    public required string SourceId { get; set; }
    public required string LinkType { get; set; }
    public required string? Context { get; set; }
    public required int StreamId { get; set; }
    public required string? StreamName { get; set; }
    public required string? TopicName { get; set; }
    public required int? MessageId { get; set; }

    [LdgSQLiteIgnore] public string TargetType => "zulip";
    [LdgSQLiteIgnore] public string TargetId => TopicName is not null
        ? $"{StreamId}:{TopicName}"
        : StreamId.ToString();
}
