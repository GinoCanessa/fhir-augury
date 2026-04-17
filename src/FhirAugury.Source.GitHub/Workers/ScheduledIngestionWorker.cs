using FhirAugury.Common.Ingestion;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Workers;

/// <summary>
/// Background service that triggers incremental ingestion at the configured interval.
/// </summary>
public class ScheduledIngestionWorker(
    GitHubIngestionPipeline pipeline,
    IOptions<GitHubServiceOptions> options,
    ILogger<ScheduledIngestionWorker> logger)
    : ScheduledIngestionWorker<GitHubIngestionPipeline>(
        pipeline, () => options.Value.SyncSchedule, () => options.Value.MinSyncAge,
        () => options.Value.IngestionPaused,
        () => options.Value.RunIngestionOnStartupOnly, logger);
