using FhirAugury.Common.Hosting;
using FhirAugury.Common.Indexing;
using FhirAugury.Source.Fhir.Configuration;
using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Indexing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Fhir.Hosting;

/// <summary>
/// Builds the FTS sidecar index in the background after Kestrel has started, so
/// <c>/health</c> reports <c>initializing</c> while the index warms up. The
/// rebuild is skipped when the index is present and the source fingerprint is
/// unchanged.
/// </summary>
public sealed class FhirStartupRebuildService(
    IHostApplicationLifetime lifetime,
    FhirSpecDatabase specDb,
    FhirSearchIndexBuilder builder,
    IIndexTracker indexTracker,
    IOptions<FhirServiceOptions> optionsAccessor,
    ILogger<FhirStartupRebuildService> logger)
    : StartupRebuildHostedService(lifetime, logger)
{
    protected override async Task RunStartupAsync(CancellationToken ct)
    {
        if (!specDb.Exists)
        {
            logger.LogWarning("Spec database not found at {Path}; skipping FTS index build", specDb.DatabasePath);
            return;
        }

        if (!optionsAccessor.Value.RebuildFtsOnStartup)
        {
            logger.LogInformation("RebuildFtsOnStartup is disabled; skipping FTS index build");
            return;
        }

        if (!builder.NeedsRebuild())
        {
            logger.LogInformation("FTS index is up to date (fingerprint unchanged); skipping rebuild");
            return;
        }

        SetPhase("building FTS index");
        indexTracker.MarkStarted("fts");
        try
        {
            int count = await Task.Run(() => builder.Build(ct), ct).ConfigureAwait(false);
            indexTracker.MarkCompleted("fts", count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            indexTracker.MarkFailed("fts", ex.Message);
            throw;
        }
    }
}
