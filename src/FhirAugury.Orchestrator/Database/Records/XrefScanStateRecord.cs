using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Orchestrator.Database.Records;

[LdgSQLiteTable("xref_scan_state")]
[LdgSQLiteIndex(nameof(SourceName))]
public partial record class XrefScanStateRecord
{
    [LdgSQLiteKey] public required int Id { get; set; }
    public required string SourceName { get; set; }
    public required string? LastCursor { get; set; }
    public required DateTimeOffset LastScanAt { get; set; }
}
