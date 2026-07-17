using FhirAugury.Common.Hosting;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Indexing;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Hosting;

/// <summary>
/// Runs the Confluence service's startup rebuild work in the background,
/// after Kestrel has already started listening.
/// </summary>
public sealed class ConfluenceStartupRebuildService(
    IHostApplicationLifetime lifetime,
    IServiceProvider services,
    IOptions<ConfluenceServiceOptions> optionsAccessor,
    ILogger<ConfluenceStartupRebuildService> logger)
    : StartupRebuildHostedService(lifetime, logger)
{
    protected override async Task RunStartupAsync(CancellationToken ct)
    {
        ConfluenceServiceOptions opts = optionsAccessor.Value;
        ConfluenceDatabase db = services.GetRequiredService<ConfluenceDatabase>();

        if (opts.ReloadFromCacheOnStartup || db.PrimaryContentTableIsEmpty())
        {
            SetPhase("rebuilding from cache");
            ConfluenceIngestionPipeline pipeline = services.GetRequiredService<ConfluenceIngestionPipeline>();
            await pipeline.RebuildFromCacheAsync(ct).ConfigureAwait(false);
            return;
        }

        if (db.TableIsEmpty("index_keywords"))
        {
            SetPhase("rebuilding BM25 index");
            services.GetRequiredService<ConfluenceIndexer>().RebuildFullIndex(ct);
        }

        if (db.TableIsEmpty("xref_jira"))
        {
            SetPhase("rebuilding cross-reference indexes");
            services.GetRequiredService<ConfluenceXRefRebuilder>().RebuildAll(ct);
        }

        if (db.TableIsEmpty("confluence_page_links"))
        {
            SetPhase("rebuilding page link index");
            services.GetRequiredService<ConfluenceLinkRebuilder>().RebuildAll(ct);
        }
    }
}
