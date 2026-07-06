using FhirAugury.Common.Configuration;
using FhirAugury.Common.Text;
using FhirAugury.Common.WorkGroups;

namespace FhirAugury.Source.GitHub.Configuration;

/// <summary>
/// Strongly-typed configuration for the GitHub source service.
/// </summary>
public class GitHubServiceOptions
{
    public const string SectionName = "GitHub";

    /// <summary>Repositories in the FhirCore category (e.g., HL7/fhir).</summary>
    public List<string>? FhirCoreRepositories { get; set; }

    /// <summary>Repositories in the UTG category (e.g., HL7/UTG).</summary>
    public List<string>? UtgRepositories { get; set; }

    /// <summary>Repositories in the FHIR Extensions Pack category.</summary>
    public List<string>? FhirExtensionsPackRepositories { get; set; }

    /// <summary>Repositories in the Incubator category.</summary>
    public List<string>? IncubatorRepositories { get; set; }

    /// <summary>Repositories in the IG category.</summary>
    public List<string>? IgRepositories { get; set; }

    /// <summary>Repositories in the JiraSpecArtifacts category (e.g., HL7/JIRA-Spec-Artifacts).</summary>
    public List<string>? JiraSpecArtifactsRepositories { get; set; }

    /// <summary>Manual cross-reference links.</summary>
    public List<string>? ManualLinks { get; set; }


    private static readonly string[] DefaultFhirCoreRepositories = ["HL7/fhir"];
    private static readonly string[] DefaultUtgRepositories = ["HL7/UTG"];
    private static readonly string[] DefaultFhirExtensionsPackRepositories = ["HL7/fhir-extensions"];

    public List<string> GetEffectiveFhirCoreRepositories() => FhirCoreRepositories ?? [.. DefaultFhirCoreRepositories];
    public List<string> GetEffectiveUtgRepositories() => UtgRepositories ?? [.. DefaultUtgRepositories];
    public List<string> GetEffectiveFhirExtensionsPackRepositories() => FhirExtensionsPackRepositories ?? [.. DefaultFhirExtensionsPackRepositories];
    public List<string> GetEffectiveIncubatorRepositories() => IncubatorRepositories ?? [];
    public List<string> GetEffectiveIgRepositories() => IgRepositories ?? [];
    public List<string> GetEffectiveJiraSpecArtifactsRepositories() => JiraSpecArtifactsRepositories ?? [];
    public List<string> GetEffectiveManualLinks() => ManualLinks ?? [];

    public bool HasExplicitEmptyFhirCoreRepositories => FhirCoreRepositories is { Count: 0 };
    public bool HasExplicitEmptyUtgRepositories => UtgRepositories is { Count: 0 };
    public bool HasExplicitEmptyFhirExtensionsPackRepositories => FhirExtensionsPackRepositories is { Count: 0 };

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

    /// <summary>
    /// Caps how many commits the very first (no prior SHA) commit-file
    /// extraction walks back from HEAD. Incremental runs (a prior SHA exists)
    /// ignore this and always walk <c>{lastSha}..HEAD</c>. A value of <c>0</c>
    /// or negative removes the cap and extracts full history — required so the
    /// BallotNotes hydration index can cover windows whose <c>since</c> commit
    /// predates the first <see cref="MaxInitialCommits"/> commits.
    /// </summary>
    public int MaxInitialCommits { get; set; } = 500;

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

        foreach (string repo in GetEffectiveFhirCoreRepositories())
            repos.Add((repo, RepoCategory.FhirCore));
        foreach (string repo in GetEffectiveUtgRepositories())
            repos.Add((repo, RepoCategory.Utg));
        foreach (string repo in GetEffectiveFhirExtensionsPackRepositories())
            repos.Add((repo, RepoCategory.FhirExtensionsPack));
        foreach (string repo in GetEffectiveIncubatorRepositories())
            repos.Add((repo, RepoCategory.Incubator));
        foreach (string repo in GetEffectiveIgRepositories())
            repos.Add((repo, RepoCategory.Ig));
        foreach (string repo in GetEffectiveJiraSpecArtifactsRepositories())
            repos.Add((repo, RepoCategory.JiraSpecArtifacts));

        return repos;
    }

    /// <summary>
    /// Returns all repository names as a flat list (for backward compatibility).
    /// </summary>
    public List<string> GetAllRepositoryNames()
    {
        List<string> repos = [];
        repos.AddRange(GetEffectiveFhirCoreRepositories());
        repos.AddRange(GetEffectiveUtgRepositories());
        repos.AddRange(GetEffectiveFhirExtensionsPackRepositories());
        repos.AddRange(GetEffectiveIncubatorRepositories());
        repos.AddRange(GetEffectiveIgRepositories());
        repos.AddRange(GetEffectiveJiraSpecArtifactsRepositories());
        return repos;
    }

    /// <summary>
    /// Master switch for repo-scoped bare-integer Jira attribution. When false,
    /// <see cref="ResolveJiraScope"/> always returns <c>null</c> and no bare
    /// numbers are resolved (prefixed/URL extraction is unaffected).
    /// </summary>
    public bool BareNumberAttributionEnabled { get; set; } = true;

    /// <summary>
    /// Default Jira project key per repository category, used by the bare-number
    /// pass when no per-repo override is present. Utg defaults to <c>UP</c>;
    /// individual Utg repos can select <c>UPSM</c> via
    /// <see cref="RepoOverrideOptions.TerminologyProjectKey"/>.
    /// </summary>
    public Dictionary<RepoCategory, string> JiraProjectKeyByCategory { get; set; } = new()
    {
        [RepoCategory.FhirCore] = "FHIR",
        [RepoCategory.FhirExtensionsPack] = "FHIR",
        [RepoCategory.Incubator] = "FHIR",
        [RepoCategory.Ig] = "FHIR",
        [RepoCategory.JiraSpecArtifacts] = "FHIR",
        [RepoCategory.Utg] = "UP",
    };

    /// <summary>
    /// Inclusive bare-number ranges per project key. A standalone integer only
    /// resolves to <c>KEY-N</c> when it falls within the key's range. Uppers for
    /// the terminology projects are held below calendar years so values like
    /// <c>2026</c> cannot resolve.
    /// </summary>
    public Dictionary<string, JiraNumberRange> JiraNumberRanges { get; set; }
        = new(StringComparer.OrdinalIgnoreCase)
        {
            ["FHIR"] = new JiraNumberRange(2839, 70000),
            ["UP"] = new JiraNumberRange(40, 2000),
            ["UPSM"] = new JiraNumberRange(10, 2000),
        };

    /// <summary>
    /// Resolves the repo-scoped <see cref="RepoJiraScope"/> for the given
    /// repository, or <c>null</c> when bare-number attribution is disabled, the
    /// repo is unknown, or the chosen project key has no configured range. The
    /// scope always contains exactly one project (UP/UPSM ranges overlap, so a
    /// multi-key first-match-wins scope would be ambiguous).
    /// </summary>
    public RepoJiraScope? ResolveJiraScope(string repoFullName)
    {
        if (!BareNumberAttributionEnabled) return null;
        if (string.IsNullOrWhiteSpace(repoFullName)) return null;

        RepoCategory? category = null;
        foreach ((string Name, RepoCategory Category) repo in GetAllRepositories())
        {
            if (string.Equals(repo.Name, repoFullName, StringComparison.OrdinalIgnoreCase))
            {
                category = repo.Category;
                break;
            }
        }
        if (category is null) return null;

        string? projectKey = null;
        if (RepoOverrides.TryGetValue(repoFullName, out RepoOverrideOptions? overrideOptions))
        {
            if (!string.IsNullOrWhiteSpace(overrideOptions.JiraProjectKey))
                projectKey = overrideOptions.JiraProjectKey.Trim();
            else if (category == RepoCategory.Utg && !string.IsNullOrWhiteSpace(overrideOptions.TerminologyProjectKey))
                projectKey = overrideOptions.TerminologyProjectKey.Trim();
        }

        if (projectKey is null && JiraProjectKeyByCategory.TryGetValue(category.Value, out string? categoryKey))
            projectKey = categoryKey;

        if (string.IsNullOrWhiteSpace(projectKey)) return null;
        if (!JiraNumberRanges.TryGetValue(projectKey, out JiraNumberRange? range)) return null;

        return new RepoJiraScope([new RepoJiraProjectScope(projectKey.ToUpperInvariant(), range.Lower, range.Upper)]);
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
    public List<string>? AdditionalSkipExtensions { get; set; }

    /// <summary>Additional directory names to skip (beyond the built-in list).</summary>
    public List<string>? AdditionalSkipDirectories { get; set; }

    /// <summary>When non-empty, only index files under these paths (relative to clone root).</summary>
    public List<string>? IncludeOnlyPaths { get; set; }

    /// <summary>
    /// Gitignore-style glob patterns for files/directories to exclude from indexing.
    /// Patterns follow .gitignore syntax: *, **, ?, negation with !, directory patterns
    /// with trailing /. Evaluated in order; last match wins.
    /// Merged with patterns from .augury-index-ignore in the repository root.
    /// </summary>
    public List<string>? IgnorePatterns { get; set; }

    private static readonly string[] DefaultIgnorePatterns =
    [
        "**/test-data/**",
        "**/testdata/**",
        "**/*.generated.*",
        "**/vendor/**",
        "**/third_party/**",
    ];

    public List<string> GetEffectiveAdditionalSkipExtensions() => AdditionalSkipExtensions ?? [];
    public List<string> GetEffectiveAdditionalSkipDirectories() => AdditionalSkipDirectories ?? [];
    public List<string> GetEffectiveIncludeOnlyPaths() => IncludeOnlyPaths ?? [];
    public List<string> GetEffectiveIgnorePatterns() => IgnorePatterns ?? [.. DefaultIgnorePatterns];
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

    /// <summary>
    /// Explicit Jira project key for repo-scoped bare-number resolution. Wins
    /// over the category default and <see cref="TerminologyProjectKey"/>.
    /// </summary>
    public string? JiraProjectKey { get; set; }

    /// <summary>
    /// For Utg repositories, selects which terminology project (<c>UP</c> or
    /// <c>UPSM</c>) a bare number resolves against. Ignored when
    /// <see cref="JiraProjectKey"/> is set.
    /// </summary>
    public string? TerminologyProjectKey { get; set; }
}

/// <summary>Inclusive numeric range a bare ticket integer may fall in.</summary>
public record JiraNumberRange(int Lower, int Upper);
