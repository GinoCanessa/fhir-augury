using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Push;

public sealed record GitPushRequest(
    string RepoFullName,
    string RepoOwner,
    string RepoName,
    string TicketKey,
    string WorktreePath);

public sealed record GitPushResult(
    bool Success,
    string? PushedCommitSha,
    string? ErrorMessage);

public interface IGitPushService
{
    Task<GitPushResult> PushAsync(GitPushRequest request, CancellationToken ct);
}

/// <summary>
/// Pushes the per-(ticket, repo) worktree branch back upstream. Builds an authenticated
/// remote URL using <see cref="ApplierAuthOptions.ResolveToken"/> when a token is
/// available; otherwise falls back to the configured remote name (presumed to be
/// pre-authenticated via SSH or stored credentials). The token is never logged or
/// returned in any response.
/// </summary>
public sealed class GitPushService(
    IGitProcessRunner git,
    IOptions<ApplierAuthOptions> authOptions,
    IOptions<ApplierOptions> applierOptions,
    ILogger<GitPushService> logger)
    : IGitPushService
{
    private readonly ApplierAuthOptions _auth = authOptions.Value;
    private readonly ApplierOptions _options = applierOptions.Value;

    public async Task<GitPushResult> PushAsync(GitPushRequest request, CancellationToken ct)
    {
        if (!Directory.Exists(request.WorktreePath))
        {
            return new GitPushResult(false, null, $"Worktree path '{request.WorktreePath}' does not exist.");
        }

        string target = ResolveRemoteTarget(request.RepoFullName);
        string args = $"push {target} {request.TicketKey}:{request.TicketKey}";
        // The rendered args are intentionally not logged because target may contain a token.
        logger.LogInformation("Pushing {Ticket} for {Repo}", request.TicketKey, request.RepoFullName);

        GitProcessResult push = await git.RunAsync(request.WorktreePath, args, ct);
        if (!push.Success)
        {
            string err = string.IsNullOrWhiteSpace(push.StdErr) ? $"git push exit {push.ExitCode}" : push.StdErr;
            return new GitPushResult(false, null, err);
        }

        GitProcessResult sha = await git.RunAsync(request.WorktreePath, "rev-parse HEAD", ct);
        if (!sha.Success)
        {
            return new GitPushResult(false, null, $"git rev-parse HEAD failed after push: {sha.StdErr}");
        }

        return new GitPushResult(true, sha.StdOut.Trim(), null);
    }

    public string ResolveRemoteTarget(string repoFullName)
    {
        string? token = _auth.ResolveToken();
        if (!string.IsNullOrEmpty(token))
        {
            return $"https://x-access-token:{token}@github.com/{repoFullName}.git";
        }
        return _options.Push.RemoteName;
    }
}
