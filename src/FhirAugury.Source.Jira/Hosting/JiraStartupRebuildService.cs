using FhirAugury.Common.Hosting;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Indexing;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FhirAugury.Source.Jira.Configuration;

namespace FhirAugury.Source.Jira.Hosting;

/// <summary>
/// Runs the Jira service's startup rebuild work in the background, after
/// Kestrel has already started listening. Mirrors the pre-Run logic that
/// previously lived in <c>Program.cs</c>.
/// </summary>
public sealed class JiraStartupRebuildService(
    IHostApplicationLifetime lifetime,
    IServiceProvider services,
    IOptions<JiraServiceOptions> optionsAccessor,
    ILogger<JiraStartupRebuildService> logger)
    : StartupRebuildHostedService(lifetime, logger)
{
    protected override async Task RunStartupAsync(CancellationToken ct)
    {
        JiraServiceOptions opts = optionsAccessor.Value;
        JiraDatabase db = services.GetRequiredService<JiraDatabase>();

        SetPhase("acquiring workgroup support file");
        string? wgXmlPath = await services.GetRequiredService<WorkGroupSupportFileAcquirer>()
            .EnsureAsync(ct).ConfigureAwait(false);

        if (wgXmlPath is not null && db.TableIsEmpty("hl7_workgroups"))
        {
            SetPhase("indexing hl7 work groups");
            services.GetRequiredService<Hl7WorkGroupIndexer>().Rebuild(wgXmlPath, ct);
        }

        if (opts.ReloadFromCacheOnStartup || db.PrimaryContentTableIsEmpty())
        {
            SetPhase("rebuilding from cache");
            JiraIngestionPipeline pipeline = services.GetRequiredService<JiraIngestionPipeline>();
            await pipeline.RebuildFromCacheAsync(ct).ConfigureAwait(false);
            return;
        }

        if (db.TableIsEmpty("jira_index_workgroups"))
        {
            SetPhase("rebuilding facet indexes");
            using SqliteConnection conn = db.OpenConnection();
            services.GetRequiredService<JiraIndexBuilder>().RebuildIndexTables(conn);
        }

        if (db.TableIsEmpty("xref_zulip"))
        {
            SetPhase("rebuilding cross-reference indexes");
            services.GetRequiredService<JiraXRefRebuilder>().RebuildAll(ct);
        }

        if (db.TableIsEmpty("index_keywords"))
        {
            SetPhase("rebuilding BM25 index");
            services.GetRequiredService<JiraIndexer>().RebuildFullIndex(ct);
        }
    }
}
