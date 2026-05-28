namespace FhirAugury.Server.Terminology.Configuration;

/// <summary>
/// Optional override / extension entry for the fhir-pkg-lib registry chain.
/// </summary>
/// <remarks>
/// v1 ships an empty list — the SDK's built-in defaults
/// (<c>packages.fhir.org</c>, public npm, HL7 fallback) cover THO. This
/// surface is here so an operator running behind a private mirror can
/// override or extend the registry chain without forking the service.
/// Order in <c>appsettings.json</c> defines query order; the SDK queries
/// configured registries before its built-in fallbacks.
/// </remarks>
public class RegistryEndpointOptions
{
    /// <summary>Human-readable name surfaced in logs.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Base URL of the npm-style registry.</summary>
    public string Url { get; set; } = string.Empty;
}
