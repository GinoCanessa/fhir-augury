using FhirAugury.Common.Hosting;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Indexing;
using FhirAugury.Source.Zulip.Ingestion;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Hosting;

/// <summary>
/// Runs the Zulip service's startup rebuild work in the background, after
/// Kestrel has already started listening.
/// </summary>
public sealed class ZulipStartupRebuildService(
    IHostApplicationLifetime lifetime,
    IServiceProvider services,
    IOptions<ZulipServiceOptions> optionsAccessor,
    ILogger<ZulipStartupRebuildService> logger)
    : StartupRebuildHostedService(lifetime, logger)
{
    protected override async Task RunStartupAsync(CancellationToken ct)
    {
        ZulipServiceOptions options = optionsAccessor.Value;
        ZulipDatabase db = services.GetRequiredService<ZulipDatabase>();

        if (options.ReloadFromCacheOnStartup || db.PrimaryContentTableIsEmpty())
        {
            SetPhase("rebuilding from cache");
            ZulipIngestionPipeline pipeline = services.GetRequiredService<ZulipIngestionPipeline>();
            await pipeline.RebuildFromCacheAsync(ct).ConfigureAwait(false);
            return;
        }

        if (db.TableIsEmpty("index_keywords"))
        {
            SetPhase("rebuilding BM25 index");
            services.GetRequiredService<ZulipIndexer>().RebuildFullIndex(ct);
        }

        if (options.ReindexTicketsOnStartup || db.TableIsEmpty("xref_jira"))
        {
            SetPhase("rebuilding cross-reference indexes");
            services.GetRequiredService<ZulipXRefRebuilder>().RebuildAll(ct);
        }
    }
}
