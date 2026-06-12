using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Tools.FhirSpecReview.Database.Records;

/// <summary>
/// One search-parameter-inventory row for a reviewed artifact, sourced from the
/// current-build FHIR R6 vocabulary (<c>cache/fhir-r6.db</c>). Keyed to the
/// parent <see cref="ArtifactRecord"/> via <see cref="ArtifactId"/>.
/// </summary>
[LdgSQLiteTable("artifact_search_parameters")]
[LdgSQLiteIndex(nameof(ArtifactId))]
public partial record class ArtifactSearchParameterRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required int ArtifactId { get; set; }

    /// <summary>The source <c>SearchParameters.Id</c>.</summary>
    public required string SearchParamId { get; set; }

    public string? Name { get; set; } = null;
    public string? Status { get; set; } = null;
    public int? FhirMaturity { get; set; } = null;
    public string? StandardsStatus { get; set; } = null;
    public bool? IsExperimental { get; set; } = null;
    public string? WorkGroup { get; set; } = null;

    /// <summary>The search-parameter type (e.g. <c>token</c>, <c>reference</c>).</summary>
    public string? SearchType { get; set; } = null;
    public string? Description { get; set; } = null;

    public int ParamOrder { get; set; } = 0;
}
