using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Server.Terminology.Database.Records;

/// <summary>
/// One row per configured THO NPM package. Tracks both the
/// requested directive (e.g. <c>"latest"</c>) and the concrete
/// version the FhirPkg SDK resolved that directive to, so a
/// <c>"latest"</c>-tracking install becomes idempotent across
/// reboots until the upstream <c>"latest"</c> moves.
/// </summary>
/// <remarks>
/// Composite uniqueness on (<see cref="PackageId"/>,
/// <see cref="ResolvedVersion"/>) is enforced by a separate
/// <c>CREATE UNIQUE INDEX</c> in <c>TerminologyDatabase</c>
/// (CsLightDbGen's <c>[LdgSQLiteIndex]</c> does not support
/// uniqueness — see the stored repo memory).
/// </remarks>
[LdgSQLiteTable("terminology_packages")]
[LdgSQLiteIndex(nameof(PackageId))]
public partial record class TerminologyPackageRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    /// <summary>NPM package id, e.g. <c>hl7.terminology.r4</c>.</summary>
    public required string PackageId { get; set; }

    /// <summary>The directive originally requested (e.g. <c>"latest"</c>, <c>"6.5.0"</c>).</summary>
    public required string RequestedVersionTag { get; set; }

    /// <summary>Concrete semver returned by the FhirPkg SDK (e.g. <c>"6.5.0"</c>).</summary>
    public required string ResolvedVersion { get; set; }

    /// <summary>Canonical FHIR major version tag (<c>R4</c>/<c>R5</c>).</summary>
    public required string FhirVersion { get; set; }

    public required DateTimeOffset IngestedAt { get; set; }

    public int ArtifactCount { get; set; } = 0;

    public int ConceptCount { get; set; } = 0;
}
