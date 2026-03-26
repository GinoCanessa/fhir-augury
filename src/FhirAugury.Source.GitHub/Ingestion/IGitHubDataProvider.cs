namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Abstracts the data-fetching mechanism for GitHub issues, PRs, comments, and repos.
/// Implementations may use the REST API (<see cref="GitHubRestProvider"/>) or the gh CLI (<see cref="GitHubCliProvider"/>).
/// </summary>
public interface IGitHubDataProvider
{
    /// <summary>The source name used for sync-state tracking and cache layout.</summary>
    static string SourceName => "github";

    /// <summary>Full download of all issues for configured repositories.</summary>
    Task<IngestionResult> DownloadAllAsync(string? repoFilter = null, CancellationToken ct = default);

    /// <summary>Incremental download since a given timestamp.</summary>
    Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Reload from cached responses (no network).</summary>
    Task<IngestionResult> LoadFromCacheAsync(CancellationToken ct = default);
}
