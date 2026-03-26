using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

[LdgSQLiteTable("jira_zulip_refs")]
[LdgSQLiteIndex(nameof(IssueKey))]
[LdgSQLiteIndex(nameof(StreamId))]
[LdgSQLiteIndex(nameof(StreamId), nameof(TopicName))]
public partial record class JiraZulipRefRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string IssueKey { get; set; }
    public required string SourceType { get; set; }
    public required string Url { get; set; }
    public required int StreamId { get; set; }
    public required string? StreamName { get; set; }
    public required string? TopicName { get; set; }
    public required int? MessageId { get; set; }
    public required string? Context { get; set; }
}
