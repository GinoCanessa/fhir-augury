using FhirAugury.Common.Ingestion;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Workers;

/// <summary>
/// Background service that triggers incremental ingestion at the configured interval.
/// </summary>
public class ScheduledIngestionWorker(
    ConfluenceIngestionPipeline pipeline,
    IOptions<ConfluenceServiceOptions> options,
    ILogger<ScheduledIngestionWorker> logger)
    : ScheduledIngestionWorker<ConfluenceIngestionPipeline>(
        pipeline, () => options.Value.SyncSchedule, () => options.Value.MinSyncAge,
        () => options.Value.IngestionPaused, logger);
