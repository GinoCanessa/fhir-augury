using FhirAugury.Common.Configuration;

namespace FhirAugury.Source.Fhir.Configuration;

/// <summary>
/// Strongly-typed configuration for the FHIR specification source service.
/// Unlike the other sources there is no ingestion/auth/sync — only a read-only
/// query surface over a fixed spec database plus a writable FTS sidecar.
/// </summary>
public class FhirServiceOptions
{
    public const string SectionName = "Fhir";

    /// <summary>Path to the read-only, upstream-built FHIR spec database.</summary>
    public string DatabasePath { get; set; } = "./cache/fhir-spec.db";

    /// <summary>Path to the writable FTS sidecar database (a disposable, rebuildable artifact).</summary>
    public string SidecarDatabasePath { get; set; } = "./data/fhir-spec-fts.db";

    /// <summary>
    /// Release token used when a request omits one. When null, resolves to the
    /// latest stable (newest non-prerelease) release in the spec database.
    /// </summary>
    public string? DefaultRelease { get; set; }

    /// <summary>
    /// When true, (re)builds the FTS sidecar index at startup when it is empty or
    /// the spec database fingerprint has changed.
    /// </summary>
    public bool RebuildFtsOnStartup { get; set; } = true;

    /// <summary>HTTP address of the orchestrator (informational; this source pushes no notifications).</summary>
    public string? OrchestratorAddress { get; set; }

    public PortConfiguration Ports { get; set; } = new() { Http = 5195 };

    public Bm25Options Bm25 { get; set; } = new();
}
