using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_ticket_topic_groups")]
[LdgSQLiteIndex(nameof(TopicRowId))]
[LdgSQLiteIndex(nameof(FirstTicketKey))]
public partial record class PreparedTicketTopicGroupRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required int TopicRowId { get; set; }
    public required string FirstTicketKey { get; set; }
    public required string Rationale { get; set; }
    public required int OrderInTopic { get; set; }
    public required DateTimeOffset SavedAt { get; set; }
}
