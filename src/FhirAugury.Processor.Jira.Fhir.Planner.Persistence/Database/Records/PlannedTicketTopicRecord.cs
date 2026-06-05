using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.Records;

[LdgSQLiteTable("planned_ticket_topics")]
[LdgSQLiteIndex(nameof(WorkGroupClean))]
[LdgSQLiteIndex(nameof(Specification))]
[LdgSQLiteIndex(nameof(Type))]
public partial record class PlannedTicketTopicRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string WorkGroupClean { get; set; }
    public required string WorkGroupDisplay { get; set; }
    public required string Specification { get; set; }
    public required string Type { get; set; }
    public required string ShortDescription { get; set; }
    public required string LongerDescription { get; set; }
    public int? RenderOrderHint { get; set; }
    public required DateTimeOffset SavedAt { get; set; }
}
