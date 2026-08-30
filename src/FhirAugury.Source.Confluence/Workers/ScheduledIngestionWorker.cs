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
    ConfluenceIngestionGate gate,
    IOptions<ConfluenceServiceOptions> options,
    ILogger<ScheduledIngestionWorker> logger)
    : ScheduledIngestionWorker<ConfluenceIngestionPipeline>(
        pipeline, () => options.Value.SyncSchedule, () => options.Value.MinSyncAge,
        // A durable block pauses the schedule exactly the way the configured
        // pause does; the generic worker never learns Confluence semantics.
        () => options.Value.IngestionPaused || gate.IsBlocked,
        () => options.Value.RunIngestionOnStartupOnly, logger);
