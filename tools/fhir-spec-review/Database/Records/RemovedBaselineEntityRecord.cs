using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Tools.FhirSpecReview.Database.Records;

/// <summary>
/// A page or artifact that exists in the published baseline site but has no
/// corresponding entry in the current build — i.e. removed since the baseline
/// release. Advisory (report-only), not a blocking check.
/// </summary>
[LdgSQLiteTable("removed_baseline_entities")]
[LdgSQLiteIndex(nameof(EntityKind))]
[LdgSQLiteIndex(nameof(WorkGroupCode))]
public partial record class RemovedBaselineEntityRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    /// <summary>Either <c>page</c> or <c>artifact</c>.</summary>
    public required string EntityKind { get; set; }

    public required string Name { get; set; }

    public required string BaselineRelease { get; set; }

    public string? WorkGroupCode { get; set; } = null;
}
