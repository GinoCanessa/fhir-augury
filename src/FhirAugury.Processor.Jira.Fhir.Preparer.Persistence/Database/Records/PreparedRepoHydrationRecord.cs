using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_repo_hydration")]
[LdgSQLiteIndex(nameof(TicketKey))]
[LdgSQLiteIndex(nameof(Repo))]
[LdgSQLiteIndex(nameof(WorkGroup))]
[LdgSQLiteIndex(nameof(Specification))]
[LdgSQLiteIndex(nameof(HydrationStatus))]
public partial record class PreparedRepoHydrationRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string TicketKey { get; set; }
    public required string Repo { get; set; }
    public string? Description { get; set; }
    public string? WorkGroup { get; set; }
    public string? Specification { get; set; }
    public string? CategoryDetail { get; set; }
    public string? Url { get; set; }
    public required DateTimeOffset HydratedAt { get; set; }
    public required string HydrationStatus { get; set; }
    public string? HydrationReason { get; set; }
}
