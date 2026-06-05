using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.Records;

[LdgSQLiteTable("planned_ticket_topic_members")]
[LdgSQLiteIndex(nameof(TopicRowId))]
[LdgSQLiteIndex(nameof(TopicGroupRowId))]
[LdgSQLiteIndex(nameof(TicketKey))]
public partial record class PlannedTicketTopicMemberRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required int TopicRowId { get; set; }
    public int? TopicGroupRowId { get; set; }
    public required string TicketKey { get; set; }
    public required int OrderInContainer { get; set; }
}
