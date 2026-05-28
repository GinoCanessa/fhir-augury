using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Server.Terminology.Database.Records;

/// <summary>
/// One concept row per CodeSystem concept or per
/// ValueSet <c>compose.include</c> concrete concept. ValueSet
/// <c>$expand</c> output is NOT pre-computed in v1.
/// </summary>
[LdgSQLiteTable("terminology_concepts")]
[LdgSQLiteIndex(nameof(ArtifactId))]
[LdgSQLiteIndex(nameof(SystemUrl), nameof(Code))]
[LdgSQLiteIndex(nameof(DisplayNormalized))]
public partial record class TerminologyConceptRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required int ArtifactId { get; set; }

    /// <summary>
    /// Owning CodeSystem canonical URL. For ValueSet rows this is the
    /// <c>compose.include.system</c> value; for CodeSystem rows it is
    /// the artifact's own canonical URL.
    /// </summary>
    /// <remarks>
    /// Stored under the property name <c>SystemUrl</c> (rather than
    /// <c>System</c>) so that CsLightDbGen's generated code does not
    /// emit a <c>System</c> identifier that collides with the
    /// <c>System.*</c> namespace.
    /// </remarks>
    public required string SystemUrl { get; set; }

    public required string Code { get; set; }

    public required string? Display { get; set; }

    /// <summary>Lowercased + punctuation-stripped form of <see cref="Display"/>.</summary>
    public required string? DisplayNormalized { get; set; }

    public required string? Definition { get; set; }

    /// <summary>
    /// JSON array of designations, normalized to a single shape
    /// across FHIR R4/R5. Empty array (<c>"[]"</c>) when none.
    /// Shape: <c>{ use?: { system?, code?, display? }, language?, value }</c>.
    /// </summary>
    public required string DesignationsJson { get; set; }

    public required bool IsRetired { get; set; }
}
