using FhirAugury.Common.Configuration;
using FhirAugury.Common.WorkGroups;

namespace FhirAugury.Source.GitHub.Configuration;

/// <summary>
/// Strongly-typed configuration for the GitHub source service.
/// </summary>
public class GitHubServiceOptions
{
    public const string SectionName = "GitHub";

    /// <summary>Repositories in the FhirCore category (e.g., HL7/fhir).</summary>
    public List<string> FhirCoreRepositories { get; set; } = ["HL7/fhir"];

    /// <summary>Repositories in the UTG category (e.g., HL7/UTG).</summary>
    public List<string> UtgRepositories { get; set; } = ["HL7/UTG"];

    /// <summary>Repositories in the FHIR Extensions Pack category.</summary>
    public List<string> FhirExtensionsPackRepositories { get; set; } = ["HL7/fhir-extensions"];

    /// <summary>Repositories in the Incubator category.</summary>
    public List<string> IncubatorRepositories { get; set; } = [];

    /// <summary>Repositories in the IG category.</summary>
    public List<string> IgRepositories { get; set; } = [];

    /// <summary>Repositories in the JiraSpecArtifacts category (e.g., HL7/JIRA-Spec-Artifacts).</summary>
    public List<string> JiraSpecArtifactsRepositories { get; set; } = [];

    /// <summary>Manual cross-reference links.</summary>
    public List<string> ManualLinks { get; set; } = [];

    /// <summary>Authentication configuration.</summary>
    public AuthConfiguration Auth { get; set; } = new();

    public string CachePath { get; set; } = "./cache";

    /// <summary>
    /// Data provider to use: "rest" (default) or "gh-cli".
    /// "rest" uses HttpClient with a PAT. "gh-cli" invokes the gh CLI tool.
    /// </summary>
    public string Provider { get; set; } = "rest";

    /// <summary>Configuration for gh CLI provider (used when Provider is "gh-cli").</summary>
    public GhCliConfiguration GhCli { get; set; } = new();

    public string DatabasePath { get; set; } = "./data/github.db";
    public string SyncSchedule { get; set; } = "02:00:00";

    /// <summary>
    /// Minimum age of the last sync before a new sync is triggered on startup.
    /// Prevents redundant downloads when services are restarted frequently.
    /// </summary>
    public string MinSyncAge { get; set; } = "04:00:00";

    /// <summary>HTTP address of the orchestrator service for ingestion notifications.</summary>
    public string? OrchestratorAddress { get; set; }

    /// <summary>
    /// When true, pauses all ingestion (scheduled and on-demand). The service remains
    /// available for queries but will not download new content.
    /// </summary>
    public bool IngestionPaused { get; set; } = false;

    /// <summary>
    /// When true, the scheduled ingestion worker runs exactly one pass at
    /// startup (honoring <see cref="MinSyncAge"/> and <see cref="IngestionPaused"/>)
    /// and then exits its loop cleanly. The service itself keeps running, so HTTP
    /// endpoints and manual ingestion remain available. Useful for local/dev
    /// runs where a continuous sync loop is not desired.
    /// </summary>
    public bool RunIngestionOnStartupOnly { get; set; } = false;

    /// <summary>
    /// When true, rebuilds the database from cached responses on startup.
    /// </summary>
    public bool ReloadFromCacheOnStartup { get; set; } = false;

    public PortConfiguration Ports { get; set; } = new() { Http = 5190 };
    public GitHubRateLimitConfiguration RateLimiting { get; set; } = new();
    public AuxiliaryDatabaseOptions AuxiliaryDatabase { get; set; } = new();
    public DictionaryDatabaseOptions DictionaryDatabase { get; set; } = new();
    public Bm25Options Bm25 { get; set; } = new();
    public FileContentIndexingOptions FileContentIndexing { get; set; } = new();

    /// <summary>
    /// Source for the authoritative HL7 work-group CodeSystem XML (mirrors
    /// the Jira source's same-named option). Materialized into
    /// <c>cache/github/_support/</c> by the GitHub ingestion pipeline at the
    /// start of every run; consumed by <c>WorkGroupResolver</c>.
    /// </summary>
    public WorkGroupSourceXmlOptions Hl7WorkGroupSourceXml { get; set; } = new();

    /// <summary>
    /// Per-repo configuration overrides keyed by <c>OWNER/Repo</c>. Currently
    /// supports an explicit work-group override that wins over derived
    /// majority-of-JIRA-Spec values in <c>RepoDefaultWorkGroupResolver</c>.
    /// </summary>
    public Dictionary<string, RepoOverrideOptions> RepoOverrides { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns all configured repositories paired with their category.
    /// </summary>
    public IReadOnlyList<(string Name, RepoCategory Category)> GetAllRepositories()
    {
        List<(string Name, RepoCategory Category)> repos = [];

        foreach (string repo in FhirCoreRepositories)
            repos.Add((repo, RepoCategory.FhirCore));
        foreach (string repo in UtgRepositories)
            repos.Add((repo, RepoCategory.Utg));
        foreach (string repo in FhirExtensionsPackRepositories)
            repos.Add((repo, RepoCategory.FhirExtensionsPack));
        foreach (string repo in IncubatorRepositories)
            repos.Add((repo, RepoCategory.Incubator));
        foreach (string repo in IgRepositories)
            repos.Add((repo, RepoCategory.Ig));
        foreach (string repo in JiraSpecArtifactsRepositories)
            repos.Add((repo, RepoCategory.JiraSpecArtifacts));

        return repos;
    }

    /// <summary>
    /// Returns all repository names as a flat list (for backward compatibility).
    /// </summary>
    public List<string> GetAllRepositoryNames()
    {
        List<string> repos = [];
        repos.AddRange(FhirCoreRepositories);
        repos.AddRange(UtgRepositories);
        repos.AddRange(FhirExtensionsPackRepositories);
        repos.AddRange(IncubatorRepositories);
        repos.AddRange(IgRepositories);
        repos.AddRange(JiraSpecArtifactsRepositories);
        return repos;
    }
}

/// <summary>Configuration for repository file content indexing.</summary>
public class FileContentIndexingOptions
{
    /// <summary>Whether file content indexing is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum file size in bytes to index (default: 512 KB).</summary>
    public int MaxFileSizeBytes { get; set; } = 512 * 1024;

    /// <summary>Maximum extracted text length per file (default: 64 KB).</summary>
    public int MaxExtractedTextLength { get; set; } = 64 * 1024;

    /// <summary>Maximum number of files to index per repository.</summary>
    public int MaxFilesPerRepo { get; set; } = 50_000;

    /// <summary>Additional file extensions to skip (beyond the built-in list).</summary>
    public List<string> AdditionalSkipExtensions { get; set; } = [];

    /// <summary>Additional directory names to skip (beyond the built-in list).</summary>
    public List<string> AdditionalSkipDirectories { get; set; } = [];

    /// <summary>When non-empty, only index files under these paths (relative to clone root).</summary>
    public List<string> IncludeOnlyPaths { get; set; } = [];

    /// <summary>
    /// Gitignore-style glob patterns for files/directories to exclude from indexing.
    /// Patterns follow .gitignore syntax: *, **, ?, negation with !, directory patterns
    /// with trailing /. Evaluated in order; last match wins.
    /// Merged with patterns from .augury-index-ignore in the repository root.
    /// </summary>
    public List<string> IgnorePatterns { get; set; } =
    [
        "**/test-data/**",
        "**/testdata/**",
        "**/*.generated.*",
        "**/vendor/**",
        "**/third_party/**",
    ];
}

public class AuthConfiguration
{
    /// <summary>GitHub personal access token (direct value).</summary>
    public string? Token { get; set; }

    /// <summary>Environment variable name containing the GitHub PAT.</summary>
    public string? TokenEnvVar { get; set; } = "GITHUB_TOKEN";

    /// <summary>Resolves the effective token from direct value or environment variable.</summary>
    public string? ResolveToken()
    {
        if (!string.IsNullOrEmpty(Token))
            return Token;

        if (!string.IsNullOrEmpty(TokenEnvVar))
            return Environment.GetEnvironmentVariable(TokenEnvVar);

        return null;
    }
}

public class GitHubRateLimitConfiguration : RateLimitConfiguration
{
    public bool RespectRateLimitHeaders { get; set; } = true;

    /// <summary>
    /// Maximum number of concurrent HTTP requests to the GitHub API. Default is 1
    /// to prevent rate-limit header races where multiple in-flight requests depart
    /// before any response updates the remaining count.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 1;
}

/// <summary>Per-repo configuration overrides (currently work-group only).</summary>
public class RepoOverrideOptions
{
    /// <summary>
    /// Free-text work-group identifier (canonical HL7 code, display name, or
    /// any input that resolves through <c>WorkGroupResolver</c>). Wins over
    /// derived per-repo defaults.
    /// </summary>
    public string? WorkGroup { get; set; }
}
