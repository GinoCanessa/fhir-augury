using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Database;
using FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Applier.Processing;
using FhirAugury.Processor.Jira.Fhir.Applier.Push;
using FhirAugury.Processor.Jira.Fhir.Applier.Workspace;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Controllers;

public static class PushStates
{
    public const string NotPushed = "NotPushed";
    public const string Pushed = "Pushed";
    public const string PushFailed = "PushFailed";
}

[ApiController]
[Route("api/v1/applied-tickets")]
[Produces("application/json")]
public sealed class AppliedTicketsController(
    AppliedTicketWriteStore writeStore,
    IGitPushService pushService,
    RepoLockManager lockManager,
    IOptions<ApplierOptions> applierOptions)
    : ControllerBase
{
    private readonly ApplierOptions _options = applierOptions.Value;

    [HttpPost("{ticketKey}/push")]
    [ProducesResponseType(typeof(AppliedTicketPushResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AppliedTicketPushResponse>> PushAppliedTicket(string ticketKey, CancellationToken ct)
    {
        AppliedTicketRecord? aggregate = await writeStore.GetAppliedTicketAsync(ticketKey, ct);
        if (aggregate is null)
        {
            return NotFound();
        }

        IReadOnlyList<AppliedTicketRepoRecord> repos = await writeStore.ListAppliedTicketReposAsync(ticketKey, ct);
        if (!repos.Any(r => !string.IsNullOrEmpty(r.CommitSha) && r.Outcome == ApplyOutcomes.Success))
        {
            return Conflict(new { message = "No applied repo has a successful local commit yet." });
        }

        Dictionary<string, ApplierRepoOptions> configByKey = _options.Repos.ToDictionary(r => r.FullName, StringComparer.OrdinalIgnoreCase);
        List<AppliedTicketRepoPushDto> results = [];
        int pushed = 0;
        int failed = 0;
        int skipped = 0;

        foreach (AppliedTicketRepoRecord repoRow in repos)
        {
            if (string.IsNullOrEmpty(repoRow.CommitSha) || repoRow.Outcome != ApplyOutcomes.Success)
            {
                skipped++;
                results.Add(new AppliedTicketRepoPushDto(
                    repoRow.RepoKey,
                    repoRow.PushState,
                    repoRow.PushedCommitSha,
                    repoRow.PushedAt,
                    repoRow.ErrorSummary ?? "skipped (no successful commit)"));
                continue;
            }

            if (!configByKey.TryGetValue(repoRow.RepoKey, out ApplierRepoOptions? repo))
            {
                skipped++;
                results.Add(new AppliedTicketRepoPushDto(
                    repoRow.RepoKey,
                    repoRow.PushState,
                    repoRow.PushedCommitSha,
                    repoRow.PushedAt,
                    "skipped (repo not configured)"));
                continue;
            }

            string worktreePath = RepoWorkspaceLayout.WorktreePath(_options.WorkingDirectory, repo.Owner, repo.Name, ticketKey);
            using IDisposable _ = await lockManager.AcquireAsync(repo.FullName, ct);
            GitPushResult result = await pushService.PushAsync(
                new GitPushRequest(repo.FullName, repo.Owner, repo.Name, ticketKey, worktreePath),
                ct);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string newState = result.Success ? PushStates.Pushed : PushStates.PushFailed;
            await writeStore.UpdatePushStateAsync(repoRow.Id, newState, result.Success ? now : null, result.PushedCommitSha, ct);

            if (result.Success)
            {
                pushed++;
            }
            else
            {
                failed++;
            }
            results.Add(new AppliedTicketRepoPushDto(
                repoRow.RepoKey,
                newState,
                result.PushedCommitSha,
                result.Success ? now : null,
                result.ErrorMessage));
        }

        return Ok(new AppliedTicketPushResponse(ticketKey, repos.Count, pushed, failed, skipped, results));
    }
}
