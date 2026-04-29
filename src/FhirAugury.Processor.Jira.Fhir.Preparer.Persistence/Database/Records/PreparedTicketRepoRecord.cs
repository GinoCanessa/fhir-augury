using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_ticket_repos")]
[LdgSQLiteIndex(nameof(TicketKey))]
[LdgSQLiteIndex(nameof(Repo))]
public partial record class PreparedTicketRepoRecord
{
    public required string Id { get; set; }
    public required string TicketKey { get; set; }
    public required string Repo { get; set; }
    public required string RepoCategory { get; set; }
    public required string Justification { get; set; }
}

