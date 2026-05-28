namespace FhirAugury.Server.Terminology.Configuration;

/// <summary>
/// Strongly-typed configuration for the Terminology server service.
/// </summary>
/// <remarks>
/// This is the Phase 1 surface — only the minimum to boot the service on
/// the canonical Server.* port range (5300) and to point the database
/// at a per-service location. Phase 2 extends this with packages,
/// embeddings provider, defaults, etc.
/// </remarks>
public class TerminologyServiceOptions
{
    public const string SectionName = "Terminology";

    /// <summary>HTTP port bindings for the service.</summary>
    public PortsSection Ports { get; set; } = new();

    /// <summary>Path to the SQLite database (will be created on first run).</summary>
    public string DatabasePath { get; set; } = "./data/server.terminology.db";

    public class PortsSection
    {
        public int Http { get; set; } = 5300;
    }
}
