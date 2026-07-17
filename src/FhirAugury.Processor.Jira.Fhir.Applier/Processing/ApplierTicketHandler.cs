using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Database;
using FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Applier.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Processing;

/// <summary>
/// End-to-end per-ticket apply orchestrator. For each repo in the planner-side plan:
/// acquires the per-repo lock, provisions a worktree from baseline, runs the agent CLI,
/// runs the build, diffs/copies output, and commits the worktree (success or failure).
///
/// Terminal queue-state contract: the queue's <c>ProcessingStatus</c> reflects only
/// transport / runtime outcome, not domain outcome. Per-(ticket, repo) success/failure
/// lives in the <c>applied_*</c> tables. The handler returns normally whenever every
/// repo's apply attempt ran to completion; it throws only on genuinely unhandled errors
/// (planner row missing, lock unacquirable, catastrophic git failure).
/// </summary>
public sealed class ApplierTicketHandler(
    PlannerReadOnlyDatabase planner,
    AppliedTicketQueueItemStore queueStore,
    AppliedTicketWriteStore writeStore,
    RepoLockManager lockManager,
    RepoWorkspaceManager workspaceManager,
    BuildCommandRunner buildRunner,
    OutputDiffer outputDiffer,
    GitWorktreeCommitService commitService,
    JiraAgentCommandRenderer renderer,
    IJiraAgentCliRunner agentRunner,
    IOptions<ApplierOptions> applierOptions,
    IOptions<JiraProcessingOptions> jiraOptions,
    IOptions<ProcessingServiceOptions> processingOptions,
    ILogger<ApplierTicketHandler> logger)
    : IProcessingWorkItemHandler<AppliedTicketQueueItemRecord>
{
    private readonly ApplierOptions _options = applierOptions.Value;
    private readonly JiraProcessingOptions _jira = jiraOptions.Value;

    public async Task ProcessAsync(AppliedTicketQueueItemRecord item, CancellationToken ct)
    {
        IReadOnlyList<PlannedRepoView> plannedRepos = planner.ListPlannedRepos(item.Key);
        if (plannedRepos.Count == 0)
        {
            logger.LogWarning("No planned repos for {Ticket}; nothing to apply.", item.Key);
            await PersistAggregateAsync(item, "Success", null, ct);
            return;
        }

        // Re-applying replaces all prior applied-* rows for this ticket atomically.
        await writeStore.DeletePriorAppliedAsync(item.Key, ct);

        string applyCompletionId = Guid.NewGuid().ToString("N");
        Dictionary<string, ApplierRepoOptions> configByKey = _options.Repos.ToDictionary(r => r.FullName, StringComparer.OrdinalIgnoreCase);
        string? sourceTicketId = planner.GetSourceTicketId(item.Key);
        string applierDbPath = Path.GetFullPath(processingOptions.Value.DatabasePath);
        string plannerDbPath = Path.GetFullPath(_options.PlannerDatabasePath);

        bool anyFailed = false;
        List<string> failureSummaries = [];

        foreach (PlannedRepoView plannedRepo in plannedRepos)
        {
            ct.ThrowIfCancellationRequested();
            if (!configByKey.TryGetValue(plannedRepo.RepoKey, out ApplierRepoOptions? repo))
            {
                logger.LogWarning("Skipping repo {Repo} for ticket {Ticket}: no Processing:Applier:Repos entry.", plannedRepo.RepoKey, item.Key);
                anyFailed = true;
                failureSummaries.Add($"{plannedRepo.RepoKey}: not configured");
                AppliedTicketRepoRecord skipped = new()
                {
                    IssueKey = item.Key,
                    RepoKey = plannedRepo.RepoKey,
                    BaselineCommitSha = string.Empty,
                    Outcome = ApplyOutcomes.RepoNotConfigured,
                    ErrorSummary = "Repo present in plan but not configured in Processing:Applier:Repos.",
                };
                await writeStore.InsertAppliedTicketRepoAsync(skipped, ct);
                continue;
            }

            (string outcome, string? errorSummary) = await ProcessRepoAsync(
                item,
                repo,
                plannedRepo,
                applyCompletionId,
                sourceTicketId,
                applierDbPath,
                plannerDbPath,
                ct);
            if (outcome != ApplyOutcomes.Success)
            {
                anyFailed = true;
                failureSummaries.Add($"{repo.FullName}: {outcome}");
            }
        }

        string aggregate = anyFailed ? "Failed" : "Success";
        string? aggregateError = anyFailed ? string.Join("; ", failureSummaries) : null;
        await PersistAggregateAsync(item, aggregate, aggregateError, ct);
    }

    private async Task<(string Outcome, string? ErrorSummary)> ProcessRepoAsync(
        AppliedTicketQueueItemRecord item,
        ApplierRepoOptions repo,
        PlannedRepoView plannedRepo,
        string applyCompletionId,
        string? sourceTicketId,
        string applierDbPath,
        string plannerDbPath,
        CancellationToken ct)
    {
        using IDisposable _ = await lockManager.AcquireAsync(repo.FullName, ct);

        string baselineSha;
        string worktreePath;
        try
        {
            baselineSha = await workspaceManager.EnsureWorktreeAsync(repo, item.Key, ct);
            worktreePath = workspaceManager.WorktreePath(repo, item.Key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await PersistRepoFailureAsync(item, repo, plannedRepo, string.Empty, ApplyOutcomes.WorktreeFailed, ex.Message, applyCompletionId, ct);
            return (ApplyOutcomes.WorktreeFailed, ex.Message);
        }

        // Agent invocation
        Dictionary<string, string> tokens = new(StringComparer.OrdinalIgnoreCase)
        {
            ["worktreePath"] = worktreePath,
            ["plannerDbPath"] = plannerDbPath,
            ["repoOwner"] = repo.Owner,
            ["repoName"] = repo.Name,
            ["baselineCommitSha"] = baselineSha,
            ["plannerCompletionId"] = item.PlannerCompletionId ?? string.Empty,
        };
        JiraAgentCommandContext agentContext = new()
        {
            TicketKey = item.Key,
            SourceTicketId = sourceTicketId ?? item.Key,
            DatabasePath = applierDbPath,
            SourceTicketShape = item.SourceTicketShape,
            ExtensionTokens = tokens,
        };

        JiraAgentResult agentResult;
        try
        {
            JiraAgentCommand command = renderer.Render(agentContext);
            agentResult = await agentRunner.RunAsync(command, agentContext, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CommitAndPersistAsync(item, repo, plannedRepo, baselineSha, worktreePath, ApplyOutcomes.AgentFailed, ex.Message, applyCompletionId, ct);
            return (ApplyOutcomes.AgentFailed, ex.Message);
        }

        if (agentResult.Canceled || agentResult.ExitCode != 0)
        {
            string err = string.IsNullOrWhiteSpace(agentResult.StderrTail) ? $"agent exit {agentResult.ExitCode}" : agentResult.StderrTail;
            await CommitAndPersistAsync(item, repo, plannedRepo, baselineSha, worktreePath, ApplyOutcomes.AgentFailed, err, applyCompletionId, ct);
            return (ApplyOutcomes.AgentFailed, err);
        }

        // Build
        BuildCommandContext buildContext = new(
            WorkingDirectory: worktreePath,
            PrimaryClonePath: workspaceManager.PrimaryClonePath(repo),
            BaselineDir: workspaceManager.BaselinePath(repo),
            OutputDir: worktreePath,
            TicketKey: item.Key,
            RepoOwner: repo.Owner,
            RepoName: repo.Name);
        BuildCommandResult buildResult;
        try
        {
            buildResult = await buildRunner.RunAsync(repo.BuildCommand, repo, buildContext, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CommitAndPersistAsync(item, repo, plannedRepo, baselineSha, worktreePath, ApplyOutcomes.BuildFailed, ex.Message, applyCompletionId, ct);
            return (ApplyOutcomes.BuildFailed, ex.Message);
        }
        if (!buildResult.Success)
        {
            string err = string.IsNullOrWhiteSpace(buildResult.StdErr) ? $"build exit {buildResult.ExitCode}" : buildResult.StdErr;
            await CommitAndPersistAsync(item, repo, plannedRepo, baselineSha, worktreePath, ApplyOutcomes.BuildFailed, err, applyCompletionId, ct);
            return (ApplyOutcomes.BuildFailed, err);
        }

        // Diff + output copy
        OutputDiffSummary diffSummary;
        try
        {
            diffSummary = await outputDiffer.ComputeAndCopyAsync(repo, worktreePath, workspaceManager.BaselinePath(repo), item.Key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CommitAndPersistAsync(item, repo, plannedRepo, baselineSha, worktreePath, ApplyOutcomes.DiffFailed, ex.Message, applyCompletionId, ct);
            return (ApplyOutcomes.DiffFailed, ex.Message);
        }

        // Commit success
        CommitContext commitContext = NewCommitContext(item, applyCompletionId, null, null);
        CommitResult commit = await commitService.CommitAsync(worktreePath, commitContext, success: true, ct);

        AppliedTicketRepoRecord repoRow = new()
        {
            IssueKey = item.Key,
            RepoKey = repo.FullName,
            BaselineCommitSha = baselineSha,
            BranchName = item.Key,
            CommitSha = commit.CommitSha,
            Outcome = ApplyOutcomes.Success,
            ErrorSummary = null,
        };
        await writeStore.InsertAppliedTicketRepoAsync(repoRow, ct);
        await PersistPlannedChangesAsync(item, repo, repoRow.Id, ApplyOutcomes.Success, null, ct);
        await PersistOutputFilesAsync(item, repo, diffSummary, ct);
        return (ApplyOutcomes.Success, null);
    }

    private async Task CommitAndPersistAsync(
        AppliedTicketQueueItemRecord item,
        ApplierRepoOptions repo,
        PlannedRepoView plannedRepo,
        string baselineSha,
        string worktreePath,
        string outcome,
        string errorSummary,
        string applyCompletionId,
        CancellationToken ct)
    {
        string? commitSha = null;
        try
        {
            CommitContext failureCtx = NewCommitContext(item, applyCompletionId, outcome, errorSummary);
            CommitResult commit = await commitService.CommitAsync(worktreePath, failureCtx, success: false, ct);
            commitSha = commit.CommitSha;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failure-path commit failed for {Repo} ticket {Ticket}; persisting without commit SHA.", repo.FullName, item.Key);
        }

        AppliedTicketRepoRecord repoRow = new()
        {
            IssueKey = item.Key,
            RepoKey = repo.FullName,
            BaselineCommitSha = baselineSha,
            BranchName = item.Key,
            CommitSha = commitSha,
            Outcome = outcome,
            ErrorSummary = errorSummary,
        };
        await writeStore.InsertAppliedTicketRepoAsync(repoRow, ct);
        await PersistPlannedChangesAsync(item, repo, repoRow.Id, outcome, errorSummary, ct);
    }

    private async Task PersistRepoFailureAsync(
        AppliedTicketQueueItemRecord item,
        ApplierRepoOptions repo,
        PlannedRepoView plannedRepo,
        string baselineSha,
        string outcome,
        string errorSummary,
        string applyCompletionId,
        CancellationToken ct)
    {
        AppliedTicketRepoRecord repoRow = new()
        {
            IssueKey = item.Key,
            RepoKey = repo.FullName,
            BaselineCommitSha = baselineSha,
            BranchName = null,
            CommitSha = null,
            Outcome = outcome,
            ErrorSummary = errorSummary,
        };
        await writeStore.InsertAppliedTicketRepoAsync(repoRow, ct);
        await PersistPlannedChangesAsync(item, repo, repoRow.Id, outcome, errorSummary, ct);
    }

    private async Task PersistPlannedChangesAsync(
        AppliedTicketQueueItemRecord item,
        ApplierRepoOptions repo,
        string ticketRepoId,
        string outcome,
        string? errorSummary,
        CancellationToken ct)
    {
        IReadOnlyList<PlannedRepoChangeView> changes = planner.ListPlannedRepoChanges(item.Key, repo.FullName);
        foreach (PlannedRepoChangeView change in changes)
        {
            AppliedTicketRepoChangeRecord row = new()
            {
                IssueKey = item.Key,
                TicketRepoId = ticketRepoId,
                RepoKey = repo.FullName,
                PlannedChangeId = change.Id,
                ChangeSequence = change.ChangeSequence,
                FilePath = change.FilePath,
                ChangeTitle = change.ChangeTitle,
                ApplyOutcome = outcome,
                ApplyErrorSummary = errorSummary,
            };
            await writeStore.InsertAppliedTicketRepoChangeAsync(row, ct);
        }
    }

    private async Task PersistOutputFilesAsync(AppliedTicketQueueItemRecord item, ApplierRepoOptions repo, OutputDiffSummary summary, CancellationToken ct)
    {
        foreach (OutputDiffEntry entry in summary.Entries)
        {
            AppliedTicketOutputFileRecord row = new()
            {
                IssueKey = item.Key,
                RepoKey = repo.FullName,
                RelativePath = entry.RelativePath,
                ByteSize = entry.ByteSize,
                Sha256 = entry.Sha256,
                DiffSummary = entry.DiffSummary,
            };
            await writeStore.InsertAppliedTicketOutputFileAsync(row, ct);
        }
    }

    private async Task PersistAggregateAsync(AppliedTicketQueueItemRecord item, string outcome, string? errorSummary, CancellationToken ct)
    {
        AppliedTicketRecord aggregate = new()
        {
            Key = item.Key,
            PlannerCompletionId = item.PlannerCompletionId ?? string.Empty,
            ApplyCompletionId = Guid.NewGuid().ToString("N"),
            Outcome = outcome,
            ErrorSummary = errorSummary,
        };
        await writeStore.UpsertAppliedTicketAsync(aggregate, ct);
        item.Outcome = outcome;
        item.ErrorSummary = errorSummary;
        await queueStore.UpdateOutcomeAsync(item, outcome, errorSummary, ct);
    }

    private static CommitContext NewCommitContext(
        AppliedTicketQueueItemRecord item,
        string applyCompletionId,
        string? failureKind,
        string? failureSummary)
    {
        return new CommitContext(
            TicketKey: item.Key,
            ApplyCompletionId: applyCompletionId,
            PlannerCompletionId: item.PlannerCompletionId ?? string.Empty,
            PlannerCompletedAt: item.PlannerCompletedAt,
            FailureKind: failureKind,
            FailureSummary: failureSummary);
    }
}

public static class ApplyOutcomes
{
    public const string Success = "Success";
    public const string AgentFailed = "AgentFailed";
    public const string BuildFailed = "BuildFailed";
    public const string DiffFailed = "DiffFailed";
    public const string WorktreeFailed = "WorktreeFailed";
    public const string RepoNotConfigured = "RepoNotConfigured";
}
