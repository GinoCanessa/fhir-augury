using FhirAugury.Common.Ingestion;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Ingestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Workers;

/// <summary>
/// Background service that triggers incremental ingestion at the configured interval.
/// </summary>
public class ScheduledIngestionWorker(
    ZulipIngestionPipeline pipeline,
    IOptions<ZulipServiceOptions> options,
    ILogger<ScheduledIngestionWorker> logger)
    : ScheduledIngestionWorker<ZulipIngestionPipeline>(
        pipeline, () => options.Value.SyncSchedule, () => options.Value.MinSyncAge,
        () => options.Value.IngestionPaused, logger);
