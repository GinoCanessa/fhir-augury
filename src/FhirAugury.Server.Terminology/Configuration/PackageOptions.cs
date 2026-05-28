namespace FhirAugury.Server.Terminology.Configuration;

/// <summary>
/// One configured THO NPM package to track.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="VersionTag"/> is whatever string the FHIR npm registry will
/// resolve via <c>IFhirPackageManager.InstallAsync("{PackageId}#{VersionTag}")</c>:
/// the literal token <c>"latest"</c>, a dist-tag, a concrete semver
/// (e.g. <c>"6.5.0"</c>), or a range expression. The Firely-pkg SDK
/// owns resolution against the configured registry chain.
/// </para>
/// <para>
/// <see cref="FhirVersion"/> selects which Firely deserializer
/// (<c>FhirR4</c> vs <c>FhirR5</c>) is used to parse the package's
/// CodeSystem/ValueSet resources. Defaults of v1 are
/// <c>hl7.terminology.r4 / R4 / latest</c> and
/// <c>hl7.terminology.r5 / R5 / latest</c>.
/// </para>
/// </remarks>
public class PackageOptions
{
    /// <summary>NPM package id, e.g. <c>hl7.terminology.r4</c>.</summary>
    public string PackageId { get; set; } = string.Empty;

    /// <summary>FHIR major version: currently <c>R4</c> or <c>R5</c>.</summary>
    public string FhirVersion { get; set; } = string.Empty;

    /// <summary>Version directive forwarded to fhir-pkg-lib.</summary>
    public string VersionTag { get; set; } = "latest";
}
