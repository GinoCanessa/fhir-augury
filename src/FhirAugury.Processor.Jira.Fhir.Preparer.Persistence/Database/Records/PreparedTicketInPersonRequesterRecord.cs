using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;

[LdgSQLiteTable("prepared_ticket_in_person_requesters")]
[LdgSQLiteIndex(nameof(TicketKey))]
public partial record class PreparedTicketInPersonRequesterRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    public required string TicketKey { get; set; }
    public required string DisplayName { get; set; }
    public int? PublicDisplayNamePolicyVersion { get; set; }
}
