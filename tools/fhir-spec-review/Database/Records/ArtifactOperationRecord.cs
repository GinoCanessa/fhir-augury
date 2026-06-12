using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Tools.FhirSpecReview.Database.Records;

/// <summary>
/// One operation-inventory row for a reviewed artifact, sourced from the
/// current-build FHIR R6 vocabulary (<c>cache/fhir-r6.db</c>). Operation-level
/// only (parameters are out of scope for v1). Keyed to the parent
/// <see cref="ArtifactRecord"/> via <see cref="ArtifactId"/>.
/// </summary>
[LdgSQLiteTable("artifact_operations")]
[LdgSQLiteIndex(nameof(ArtifactId))]
public partial record class ArtifactOperationRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required int ArtifactId { get; set; }

    /// <summary>The source <c>Operations.Id</c> (e.g. <c>Patient-match</c>).</summary>
    public required string OperationId { get; set; }

    public string? Code { get; set; } = null;
    public string? Name { get; set; } = null;

    /// <summary>The operation kind (e.g. <c>operation</c>, <c>query</c>).</summary>
    public string? OperationKind { get; set; } = null;

    public string? Status { get; set; } = null;
    public string? StandardsStatus { get; set; } = null;
    public int? FhirMaturity { get; set; } = null;
    public bool? IsExperimental { get; set; } = null;
    public string? WorkGroup { get; set; } = null;
    public string? Description { get; set; } = null;

    public int OperationOrder { get; set; } = 0;
}
