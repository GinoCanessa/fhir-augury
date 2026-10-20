using FhirAugury.Common;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Abstracts the data-fetching mechanism for GitHub issues, PRs, comments, and repos.
/// Implementations may use the REST API (<see cref="GitHubRestProvider"/>) or the gh CLI (<see cref="GitHubCliProvider"/>).
/// </summary>
public interface IGitHubDataProvider
{
    /// <summary>The source name used for sync-state tracking and cache layout.</summary>
    static string SourceName => SourceSystems.GitHub;

    /// <summary>Full download of all issues for configured repositories.</summary>
    Task<IngestionResult> DownloadAllAsync(string? repoFilter = null, CancellationToken ct = default);

    /// <summary>Incremental download since a given timestamp.</summary>
    Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, CancellationToken ct = default);

    /// <summary>
    /// Full per-repo history backfill: fetches the complete PR/issue history with
    /// no <c>updated:&gt;=</c> bound. Used once per repo to close the historical
    /// coverage gap that the moving incremental window can never reach.
    /// </summary>
    /// <param name="repoFilter">Restricts the run to a single repo when supplied.</param>
    /// <param name="resumeFrom">
    /// Progress from an earlier interrupted or partially-failed pass. Items above the
    /// cursor's watermark skip their detail fetch, and numbers in its pending-retry set are
    /// re-attempted. Null starts from the top.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// The implementation owns terminal state: only it knows whether each phase enumerated
    /// to exhaustion, so it — not the pipeline — writes the completion marker.
    /// </remarks>
    Task<IngestionResult> DownloadBackfillAsync(
        string? repoFilter = null,
        GitHubBackfillCursor? resumeFrom = null,
        CancellationToken ct = default);

    /// <summary>Reload from cached responses (no network).</summary>
    Task<IngestionResult> LoadFromCacheAsync(CancellationToken ct = default);
}
