using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.Jira.Fhir.Hydration.Common;

/// <summary>
/// Owns the public per-ticket <see cref="HydrateAsync"/> contract.
/// Pulls agent-side related keys via the target, dispatches HTTP
/// fetches via the shared fetcher, then persists the neutral batch
/// back through the target. Mirrors the exception-swallowing contract
/// of the original preparer <c>PreparedTicketHydrator.HydrateAsync</c>:
/// never throws except for <see cref="OperationCanceledException"/>.
/// </summary>
public class HydrationCoordinator(
    IHydrationTargetDatabase target,
    OrchestratorHydrationFetcher fetcher,
    ILogger logger)
{
    public virtual async Task HydrateAsync(string ticketKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(ticketKey);
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        try
        {
            IReadOnlyList<string> agentJiraKeys = await target.ListRelatedJiraKeysForTicketAsync(ticketKey, ct);
            IReadOnlyList<string> agentZulipThreadIds = await target.ListRelatedZulipThreadIdsForTicketAsync(ticketKey, ct);
            IReadOnlyList<string> agentGitHubIds = await target.ListRelatedGitHubItemIdsForTicketAsync(ticketKey, ct);
            IReadOnlyList<string> agentRepos = await target.ListReposForTicketAsync(ticketKey, ct);

            (HydrationTicketRow parentRow, List<HydrationJiraXrefRow> xrefRows) =
                await fetcher.FetchParentAsync(ticketKey, hydratedAt, ct);

            HashSet<string> jiraTargets = new(StringComparer.Ordinal);
            // Always include the self-key so a self-Jira row exists
            // regardless of whether the agent emitted it under
            // RelatedJiraKeys. Existing controllers (preparer
            // PreparedTicketHydrationController, the planner's future
            // equivalent) read self-rows where JiraKey == TicketKey for
            // workgroup-display projections.
            jiraTargets.Add(ticketKey);
            foreach (string key in agentJiraKeys)
            {
                jiraTargets.Add(key);
            }
            foreach (HydrationJiraXrefRow xref in xrefRows)
            {
                jiraTargets.Add(xref.JiraKey);
            }

            List<HydrationJiraRow> jiraRows = [];
            foreach (string jiraKey in jiraTargets.OrderBy(k => k, StringComparer.Ordinal))
            {
                jiraRows.Add(await fetcher.FetchJiraAsync(ticketKey, jiraKey, hydratedAt, ct));
            }

            List<HydrationZulipRow> zulipRows = [];
            foreach (string threadId in agentZulipThreadIds)
            {
                zulipRows.Add(await fetcher.FetchZulipAsync(ticketKey, threadId, hydratedAt, ct));
            }

            List<HydrationGitHubRow> githubRows = [];
            foreach (string itemId in agentGitHubIds)
            {
                githubRows.Add(await fetcher.FetchGitHubAsync(ticketKey, itemId, hydratedAt, ct));
            }

            List<HydrationRepoRow> repoRows = [];
            foreach (string repo in agentRepos)
            {
                repoRows.Add(await fetcher.FetchRepoAsync(ticketKey, repo, hydratedAt, ct));
            }

            HydrationBatch batch = new(
                TicketKey: ticketKey,
                Parent: parentRow,
                JiraRows: jiraRows,
                ZulipRows: zulipRows,
                GitHubRows: githubRows,
                RepoRows: repoRows,
                JiraXrefRows: xrefRows);

            await target.SaveHydrationAsync(batch, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Hydration failed for {TicketKey}; leaving prior hydration state intact.", ticketKey);
        }
    }
}
