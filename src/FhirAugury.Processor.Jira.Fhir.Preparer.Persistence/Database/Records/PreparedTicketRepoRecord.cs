using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_ticket_repos")]
[LdgSQLiteIndex(nameof(TicketKey))]
[LdgSQLiteIndex(nameof(Repo))]
public partial record class PreparedTicketRepoRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public required string TicketKey { get; set; }
    public required string Repo { get; set; }
    public required string RepoCategory { get; set; }
    public required string Justification { get; set; }
}

