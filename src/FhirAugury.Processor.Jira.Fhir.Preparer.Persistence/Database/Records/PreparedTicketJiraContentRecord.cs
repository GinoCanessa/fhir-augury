using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_ticket_jira_content")]
public partial record class PreparedTicketJiraContentRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string TicketKey { get; set; }

    public string? DescriptionHtml { get; set; }
    public string? ResolutionDescriptionHtml { get; set; }
}
