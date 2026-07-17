using FhirAugury.Source.GitHub.Cache;
using FhirAugury.Source.GitHub.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedAcquirer = FhirAugury.Common.WorkGroups.WorkGroupSupportFileAcquirer;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Thin GitHub-source wrapper around the shared
/// <see cref="FhirAugury.Common.WorkGroups.WorkGroupSupportFileAcquirer"/>.
/// Resolves the cache layout from <see cref="GitHubServiceOptions"/> and
/// <see cref="GitHubCacheLayout"/>; the actual acquisition logic lives in
/// the shared acquirer.
/// </summary>
public sealed class GitHubWorkGroupSupportFileAcquirer
{
    /// <summary>Named HttpClient used by this acquirer.</summary>
    public const string HttpClientName = SharedAcquirer.HttpClientName;

    private readonly SharedAcquirer _shared;

    public GitHubWorkGroupSupportFileAcquirer(
        IOptions<GitHubServiceOptions> optionsAccessor,
        IHttpClientFactory httpClientFactory,
        ILogger<GitHubWorkGroupSupportFileAcquirer> logger)
    {
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        GitHubServiceOptions opts = optionsAccessor.Value;
        _shared = new SharedAcquirer(
            cacheRoot: opts.CachePath,
            sourceSubdir: GitHubCacheLayout.SourceName,
            supportSubdir: GitHubCacheLayout.SupportPrefix,
            cfg: opts.Hl7WorkGroupSourceXml,
            httpClientFactory: httpClientFactory,
            logger: logger);
    }

    /// <summary>
    /// Ensures the configured XML file exists under
    /// <c>cache/github/_support/&lt;Filename&gt;</c>. Returns the absolute
    /// destination path when present, or <c>null</c>.
    /// </summary>
    public Task<string?> EnsureAsync(CancellationToken ct = default)
        => _shared.EnsureAsync(ct);
}
