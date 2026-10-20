using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Processing.Common.Database.Records;

[LdgSQLiteTable("authoring_run_input_provenance")]
[LdgSQLiteIndex(nameof(RunId))]
[LdgSQLiteIndex(nameof(Source))]
public partial record class AuthoringRunInputProvenanceRecord
{
    [LdgSQLiteKey]
    public int RowId { get; set; }

    public required string RunId { get; set; }
    public required string Source { get; set; }
    public DateTimeOffset? LatestSuccessfulRefreshAt { get; set; }
    public long? ContentRevision { get; set; }
    public required DateTimeOffset CapturedAt { get; set; }
}
