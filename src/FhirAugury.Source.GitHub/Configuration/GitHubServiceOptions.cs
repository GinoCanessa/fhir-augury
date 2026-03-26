using FhirAugury.Common.Configuration;

namespace FhirAugury.Source.GitHub.Configuration;

/// <summary>
/// Strongly-typed configuration for the GitHub source service.
/// </summary>
public class GitHubServiceOptions
{
    public const string SectionName = "GitHub";

    /// <summary>Repository discovery mode: core, explicit, or all.</summary>
    public string RepoMode { get; set; } = "core";

    /// <summary>Explicit repositories to track (owner/repo format).</summary>
    public List<string> Repositories { get; set; } = ["HL7/fhir"];

    /// <summary>Additional repositories beyond the mode-selected set.</summary>
    public List<string> AdditionalRepositories { get; set; } = [];

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

    /// <summary>gRPC address of the orchestrator service for ingestion notifications.</summary>
    public string? OrchestratorGrpcAddress { get; set; }

    /// <summary>
    /// When true, pauses all ingestion (scheduled and on-demand). The service remains
    /// available for queries but will not download new content.
    /// </summary>
    public bool IngestionPaused { get; set; } = false;

    /// <summary>
    /// When true, rebuilds the database from cached responses on startup.
    /// </summary>
    public bool ReloadFromCacheOnStartup { get; set; } = false;

    public PortConfiguration Ports { get; set; } = new() { Http = 5190, Grpc = 5191 };
    public GitHubRateLimitConfiguration RateLimiting { get; set; } = new();
    public AuxiliaryDatabaseOptions AuxiliaryDatabase { get; set; } = new();
    public DictionaryDatabaseOptions DictionaryDatabase { get; set; } = new();
    public Bm25Options Bm25 { get; set; } = new();
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
