using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.Records;

/// <summary>
/// First-class repo set for a coordinated planner topic ("core +
/// extensions + UTG"). Distinct from per-ticket
/// <c>planned_ticket_repos</c> rows: the latter list every repo a single
/// ticket touches, while this table captures the coordination set that
/// defines the topic's scope. Composite-unique on
/// <c>(TopicRowId, RepoKey)</c> via a follow-on
/// <c>CREATE UNIQUE INDEX IF NOT EXISTS</c> in
/// <c>PlannerDatabase.EnsureSchema</c> (CsLightDbGen has no
/// <c>Unique</c> property on <c>LdgSQLiteIndex</c>).
/// </summary>
[LdgSQLiteTable("planned_ticket_topic_repos")]
[LdgSQLiteIndex(nameof(TopicRowId))]
[LdgSQLiteIndex(nameof(RepoKey))]
public partial record class PlannedTicketTopicRepoRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required int TopicRowId { get; set; }
    public required string RepoKey { get; set; }
    public required int OrderInTopic { get; set; }
}
