namespace FhirAugury.Server.Terminology.Configuration;

/// <summary>
/// Strongly-typed configuration for the Terminology server service.
/// </summary>
public class TerminologyServiceOptions
{
    public const string SectionName = "Terminology";

    /// <summary>HTTP port bindings for the service.</summary>
    public PortsSection Ports { get; set; } = new();

    /// <summary>Path to the SQLite database (will be created on first run).</summary>
    public string DatabasePath { get; set; } = "./data/server.terminology.db";

    /// <summary>
    /// Root directory the FhirPkg package cache writes into. Defaults to
    /// <c>./cache/fhir-packages</c> — kept inside the service's data dir
    /// instead of <c>~/.fhir/packages</c> so cache lifecycle matches the
    /// container's data volume.
    /// </summary>
    public string CachePath { get; set; } = "./cache/fhir-packages";

    /// <summary>THO NPM packages to download / index.</summary>
    public List<PackageOptions> Packages { get; set; } = [
        new PackageOptions { PackageId = "hl7.terminology.r4", FhirVersion = "R4", VersionTag = "latest" },
        new PackageOptions { PackageId = "hl7.terminology.r5", FhirVersion = "R5", VersionTag = "latest" },
    ];

    /// <summary>
    /// Optional extra/override npm registries. Empty by default; the
    /// SDK falls back to its built-in chain (<c>packages.fhir.org</c>,
    /// public npm, HL7 fallback).
    /// </summary>
    public List<RegistryEndpointOptions> Registries { get; set; } = [];

    /// <summary>
    /// Hard cap on the number of concepts a submission may contain
    /// (sum across all submitted resources). Requests exceeding this
    /// limit are rejected with 400.
    /// </summary>
    public int MaxSubmissionConcepts { get; set; } = 25000;

    public DefaultsOptions Defaults { get; set; } = new();

    public EmbeddingsOptions Embeddings { get; set; } = new();

    public LexicalWeightsOptions LexicalWeights { get; set; } = new();

    public HybridWeightsOptions HybridWeights { get; set; } = new();

    public class PortsSection
    {
        public int Http { get; set; } = 5300;
    }

    /// <summary>
    /// Validates the bound configuration. Returns one human-readable
    /// message per error; an empty enumeration means the configuration
    /// is acceptable.
    /// </summary>
    public IEnumerable<string> Validate()
    {
        if (Ports.Http <= 0 || Ports.Http > 65535)
        {
            yield return $"Ports.Http must be in 1..65535 (got {Ports.Http}).";
        }

        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            yield return "DatabasePath must be non-empty.";
        }

        if (string.IsNullOrWhiteSpace(CachePath))
        {
            yield return "CachePath must be non-empty.";
        }

        if (MaxSubmissionConcepts < 1)
        {
            yield return $"MaxSubmissionConcepts must be >= 1 (got {MaxSubmissionConcepts}).";
        }

        if (Packages.Count == 0)
        {
            yield return "At least one entry under Packages is required.";
        }

        HashSet<string> seenPackageIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (PackageOptions pkg in Packages)
        {
            if (string.IsNullOrWhiteSpace(pkg.PackageId))
            {
                yield return "Packages[*].PackageId must be non-empty.";
                continue;
            }

            if (!seenPackageIds.Add(pkg.PackageId))
            {
                yield return $"Packages contains duplicate PackageId '{pkg.PackageId}'.";
            }

            if (string.IsNullOrWhiteSpace(pkg.VersionTag))
            {
                yield return $"Package '{pkg.PackageId}': VersionTag must be non-empty.";
            }

            if (!FhirMajorVersionParser.TryParse(pkg.FhirVersion, out _))
            {
                yield return $"Package '{pkg.PackageId}': FhirVersion '{pkg.FhirVersion}' is not supported (use 'R4' or 'R5').";
            }
        }

        foreach (RegistryEndpointOptions reg in Registries)
        {
            if (string.IsNullOrWhiteSpace(reg.Url))
            {
                yield return "Registries[*].Url must be non-empty.";
            }
        }

        if (Defaults.Limit < 1 || Defaults.Limit > 1000)
        {
            yield return $"Defaults.Limit must be in 1..1000 (got {Defaults.Limit}).";
        }

        if (Defaults.MinScore < 0.0 || Defaults.MinScore > 1.0)
        {
            yield return $"Defaults.MinScore must be in 0.0..1.0 (got {Defaults.MinScore}).";
        }

        string mode = Defaults.Mode?.Trim().ToLowerInvariant() ?? string.Empty;
        if (mode is not ("lexical" or "embeddings" or "hybrid"))
        {
            yield return $"Defaults.Mode '{Defaults.Mode}' is not supported (use 'lexical', 'embeddings', or 'hybrid').";
        }

        // v1 ships NullEmbeddingProvider only; reject anything else so
        // operators see the misconfiguration at startup rather than
        // discovering it via a 500 on /check.
        string provider = Embeddings.Provider?.Trim().ToLowerInvariant() ?? string.Empty;
        if (provider != "none")
        {
            yield return $"Embeddings.Provider '{Embeddings.Provider}' is not supported in this release (only 'none' is currently shipped).";
        }

        double weightSum = HybridWeights.Lexical + HybridWeights.Embeddings;
        if (Math.Abs(weightSum - 1.0) > 0.0001)
        {
            yield return $"HybridWeights.Lexical + HybridWeights.Embeddings must sum to 1.0 (got {weightSum}).";
        }
    }
}

