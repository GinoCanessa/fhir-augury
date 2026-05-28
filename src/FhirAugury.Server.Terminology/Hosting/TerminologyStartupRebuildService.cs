using FhirAugury.Common.Hosting;
using FhirAugury.Server.Terminology.Ingestion;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Server.Terminology.Hosting;

/// <summary>
/// Runs the THO ingestion pipeline once at startup (after Kestrel binds)
/// and publishes progress to the <see cref="TerminologyIndexStatusTracker"/>.
/// </summary>
/// <remarks>
/// The same status tracker is later updated by ad-hoc refresh requests
/// posted to <c>POST /refresh</c>.
/// </remarks>
public sealed class TerminologyStartupRebuildService(
    IHostApplicationLifetime lifetime,
    IServiceProvider services,
    TerminologyIndexStatusTracker tracker,
    ILogger<TerminologyStartupRebuildService> logger)
    : StartupRebuildHostedService(lifetime, logger)
{
    protected override async Task RunStartupAsync(CancellationToken ct)
    {
        tracker.BeginRefresh();
        SetPhase("ingesting THO packages");
        tracker.SetPhase("ingesting THO packages");

        try
        {
            TerminologyIngestionPipeline pipeline =
                services.GetRequiredService<TerminologyIngestionPipeline>();

            await pipeline.RunAsync(
                phaseSink: p =>
                {
                    SetPhase(p);
                    tracker.SetPhase(p);
                },
                ct: ct).ConfigureAwait(false);

            tracker.Complete();
        }
        catch (Exception ex)
        {
            tracker.Fail(ex);
            throw;
        }
    }
}
