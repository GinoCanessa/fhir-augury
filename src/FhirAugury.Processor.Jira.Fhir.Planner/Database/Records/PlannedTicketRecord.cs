using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Database.Records;

[LdgSQLiteTable("planned_tickets")]
public partial record class PlannedTicketRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [LdgSQLiteUnique]
    public required string Key { get; set; }

    public string Resolution { get; set; } = string.Empty;
    public string ResolutionSummary { get; set; } = string.Empty;
    public string FeatureProposal { get; set; } = string.Empty;
    public string DesignRationale { get; set; } = string.Empty;
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
}
