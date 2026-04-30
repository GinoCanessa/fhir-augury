using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Workspace;

public sealed record CommitContext(
    string TicketKey,
    string ApplyCompletionId,
    string PlannerCompletionId,
    DateTimeOffset? PlannerCompletedAt,
    string? FailureKind,
    string? FailureSummary);

public sealed record CommitResult(string CommitSha, string RenderedMessage);

/// <summary>
/// Runs <c>git -c user.* add -A</c> + <c>git commit --allow-empty -m &lt;message&gt;</c> in the
/// worktree, then captures the resulting commit SHA. Always commits — including on
/// failure — so post-mortem reviewers can see exactly what state was produced.
/// </summary>
public sealed class GitWorktreeCommitService(
    IGitProcessRunner git,
    IOptions<ApplierOptions> applierOptions,
    ILogger<GitWorktreeCommitService> logger)
{
    private readonly ApplierOptions _options = applierOptions.Value;

    public async Task<CommitResult> CommitAsync(string worktreePath, CommitContext context, bool success, CancellationToken ct)
    {
        ApplierCommitOptions commit = _options.Commit;
        string template = success ? commit.MessageTemplate : commit.FailureMessageTemplate;
        string message = RenderMessage(template, context);

        GitProcessResult addResult = await git.RunAsync(worktreePath, "add -A", ct);
        if (!addResult.Success)
        {
            throw new InvalidOperationException($"git add -A failed in {worktreePath}: {addResult.StdErr}");
        }

        string identityArgs = $"-c user.name=\"{commit.AuthorName}\" -c user.email=\"{commit.AuthorEmail}\"";
        string escapedMessage = message.Replace("\"", "\\\"");
        GitProcessResult commitResult = await git.RunAsync(
            worktreePath,
            $"{identityArgs} commit --allow-empty -m \"{escapedMessage}\"",
            ct);
        if (!commitResult.Success)
        {
            throw new InvalidOperationException($"git commit failed in {worktreePath}: {commitResult.StdErr}");
        }

        GitProcessResult shaResult = await git.RunAsync(worktreePath, "rev-parse HEAD", ct);
        if (!shaResult.Success)
        {
            throw new InvalidOperationException($"git rev-parse HEAD failed in {worktreePath}: {shaResult.StdErr}");
        }
        string sha = shaResult.StdOut.Trim();

        logger.LogInformation("Committed {Sha} in worktree {Path} (success={Success})", sha, worktreePath, success);
        return new CommitResult(sha, message);
    }

    public static string RenderMessage(string template, CommitContext context)
    {
        return template
            .Replace("{ticketKey}", context.TicketKey)
            .Replace("{applyCompletionId}", context.ApplyCompletionId)
            .Replace("{plannerCompletionId}", context.PlannerCompletionId)
            .Replace("{plannerCompletedAt}", context.PlannerCompletedAt?.ToString("O") ?? string.Empty)
            .Replace("{failureKind}", context.FailureKind ?? string.Empty)
            .Replace("{failureSummary}", context.FailureSummary ?? string.Empty);
    }
}
