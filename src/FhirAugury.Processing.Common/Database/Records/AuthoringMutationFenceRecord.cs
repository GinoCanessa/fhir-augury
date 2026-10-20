using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processing.Common.Database.Records;

[LdgSQLiteTable("authoring_mutation_fences")]
public partial record class AuthoringMutationFenceRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string ProcessorKind { get; set; }

    public required string RunId { get; set; }
    public required string LeaseId { get; set; }
    public required DateTimeOffset AcquiredAt { get; set; }
}
