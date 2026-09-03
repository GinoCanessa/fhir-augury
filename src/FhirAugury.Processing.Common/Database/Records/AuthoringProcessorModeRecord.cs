using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processing.Common.Database.Records;

[LdgSQLiteTable("authoring_processor_modes")]
public partial record class AuthoringProcessorModeRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    [LdgSQLiteUnique]
    public required string ProcessorKind { get; set; }

    public required string Mode { get; set; }
    public long Epoch { get; set; }
    public bool RevalidationRequired { get; set; }
    public string? RevalidationRunId { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
