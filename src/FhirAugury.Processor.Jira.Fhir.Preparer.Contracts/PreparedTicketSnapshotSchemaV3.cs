using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;

public static class PreparedTicketSnapshotSchemaV3
{
    public const int Version = 3;
    public const string ProcessorKind = PreparedTicketSnapshotSchemaV2.ProcessorKind;

    public static AuthoringSnapshotSchemaCatalog Catalog { get; } = new(
        Version,
        PreparedTicketSnapshotSchemaV2.Tables.Select(table =>
            table.Name switch
            {
                "prepared_ticket_hydration" or
                "prepared_jira_hydration" or
                "prepared_ticket_in_person_requesters" => new(
                    table.Name,
                    [
                        .. table.Columns,
                        "PublicDisplayNamePolicyVersion",
                    ]),
                _ => table,
            }),
        PreparedTicketSnapshotSchemaV2.CountedTables);

    public static IReadOnlyList<AuthoringSnapshotTableSchema> Tables =>
        Catalog.Tables;

    public static IReadOnlyList<string> CountedTables =>
        Catalog.CountedTables;
}
