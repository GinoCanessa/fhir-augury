using FhirAugury.Common.Hosting;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Indexing;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Hosting;

/// <summary>
/// Runs the GitHub service's startup rebuild work in the background, after
/// Kestrel has already started listening.
/// </summary>
public sealed class GitHubStartupRebuildService(
    IHostApplicationLifetime lifetime,
    IServiceProvider services,
    IOptions<GitHubServiceOptions> optionsAccessor,
    ILogger<GitHubStartupRebuildService> logger)
    : StartupRebuildHostedService(lifetime, logger)
{
    protected override async Task RunStartupAsync(CancellationToken ct)
    {
        GitHubServiceOptions opts = optionsAccessor.Value;
        GitHubDatabase db = services.GetRequiredService<GitHubDatabase>();

        if (opts.ReloadFromCacheOnStartup || db.PrimaryContentTableIsEmpty())
        {
            SetPhase("rebuilding from cache");
            GitHubIngestionPipeline pipeline = services.GetRequiredService<GitHubIngestionPipeline>();
            await pipeline.RebuildFromCacheAsync(ct).ConfigureAwait(false);
            return;
        }

        if (db.TableIsEmpty("xref_jira"))
        {
            SetPhase("rebuilding cross-reference indexes");
            GitHubXRefRebuilder xrefRebuilder = services.GetRequiredService<GitHubXRefRebuilder>();
            List<string> repos = opts.GetAllRepositoryNames();
            xrefRebuilder.RebuildAllRepos(repos, validJiraNumbers: null, ct);
        }

        if (db.TableIsEmpty("index_keywords"))
        {
            SetPhase("rebuilding BM25 index");
            services.GetRequiredService<GitHubIndexer>().RebuildFullIndex(ct);
        }
    }
}
