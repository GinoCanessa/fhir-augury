using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Tools.FhirSpecReview.Database.Records;

/// <summary>
/// One element-review row for a reviewed artifact, sourced from the current-build
/// FHIR R6 vocabulary (<c>cache/fhir-r6.db</c>). Mirrors the legacy "Element
/// Review" table columns. Keyed to the parent <see cref="ArtifactRecord"/> via
/// <see cref="ArtifactId"/>.
/// </summary>
[LdgSQLiteTable("artifact_elements")]
[LdgSQLiteIndex(nameof(ArtifactId))]
public partial record class ArtifactElementRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required int ArtifactId { get; set; }

    public required string Path { get; set; }

    public bool IsRequired { get; set; } = false;

    /// <summary>The element's max cardinality token (e.g. <c>1</c>, <c>*</c>).</summary>
    public string? MaxCardinality { get; set; } = null;

    public bool IsTrialUse { get; set; } = false;

    public bool HasFixed { get; set; } = false;
    public bool HasPattern { get; set; } = false;

    public bool RequiredBinding { get; set; } = false;
    public string? RequiredBindingValueSet { get; set; } = null;
    public bool ExternalRequiredBinding { get; set; } = false;

    public string? MeaningWhenMissing { get; set; } = null;

    public bool IsModifier { get; set; } = false;

    /// <summary>Preserves the source <c>ResourceFieldOrder</c> for stable display.</summary>
    public int ElementOrder { get; set; } = 0;
}
