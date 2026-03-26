using FhirAugury.Common.Ingestion;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Workers;

/// <summary>
/// Background service that triggers incremental ingestion at the configured interval.
/// </summary>
public class ScheduledIngestionWorker(
    JiraIngestionPipeline pipeline,
    IOptions<JiraServiceOptions> options,
    ILogger<ScheduledIngestionWorker> logger)
    : ScheduledIngestionWorker<JiraIngestionPipeline>(
        pipeline, () => options.Value.SyncSchedule, () => options.Value.MinSyncAge,
        () => options.Value.IngestionPaused, logger);
