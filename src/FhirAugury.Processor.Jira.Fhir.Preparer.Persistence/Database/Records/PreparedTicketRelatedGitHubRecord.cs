using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_ticket_related_github")]
[LdgSQLiteIndex(nameof(TicketKey))]
[LdgSQLiteIndex(nameof(GitHubItemId))]
public partial record class PreparedTicketRelatedGitHubRecord
{
    public required string Id { get; set; }
    public required string TicketKey { get; set; }
    public required string GitHubItemId { get; set; }
    public required string Justification { get; set; }
}

