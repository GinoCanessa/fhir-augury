using FhirAugury.Source.Jira.Cache;
using FhirAugury.Source.Jira.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedAcquirer = FhirAugury.Common.WorkGroups.WorkGroupSupportFileAcquirer;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Thin Jira-source wrapper around the shared
/// <see cref="FhirAugury.Common.WorkGroups.WorkGroupSupportFileAcquirer"/>.
/// Resolves the cache layout from <see cref="JiraServiceOptions"/> and
/// <see cref="JiraCacheLayout"/>; the actual acquisition logic lives in
/// the shared acquirer.
/// </summary>
public sealed class WorkGroupSupportFileAcquirer
{
    /// <summary>Named HttpClient used by this acquirer.</summary>
    public const string HttpClientName = SharedAcquirer.HttpClientName;

    private readonly SharedAcquirer _shared;

    public WorkGroupSupportFileAcquirer(
        IOptions<JiraServiceOptions> optionsAccessor,
        IHttpClientFactory httpClientFactory,
        ILogger<WorkGroupSupportFileAcquirer> logger)
    {
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        JiraServiceOptions opts = optionsAccessor.Value;
        _shared = new SharedAcquirer(
            cacheRoot: opts.CachePath,
            sourceSubdir: JiraCacheLayout.SourceName,
            supportSubdir: JiraCacheLayout.SupportPrefix,
            cfg: opts.Hl7WorkGroupSourceXml,
            httpClientFactory: httpClientFactory,
            logger: logger);
    }

    /// <summary>
    /// Ensures the configured XML file exists under
    /// <c>cache/jira/_support/&lt;Filename&gt;</c>. Returns the absolute
    /// destination path when present, or <c>null</c>.
    /// </summary>
    public Task<string?> EnsureAsync(CancellationToken ct = default)
        => _shared.EnsureAsync(ct);
}
