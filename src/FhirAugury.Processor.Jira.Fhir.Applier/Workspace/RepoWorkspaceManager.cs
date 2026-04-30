using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Database;
using FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Workspace;

public sealed record BaselineRebuildResult(string CommitSha, DateTimeOffset BuiltAt, int FilesSnapshotted);

/// <summary>
/// Owns the per-repo on-disk lifecycle: primary clone management, baseline build +
/// snapshot, and per-(ticket, repo) git worktree provisioning. <see cref="RepoLockManager"/>
/// must be acquired by the caller for any operation that mutates repo state.
/// </summary>
public sealed class RepoWorkspaceManager(
    IGitProcessRunner git,
    BuildCommandRunner buildRunner,
    RepoBaselineStore baselineStore,
    IOptions<ApplierOptions> applierOptions,
    IOptions<ApplierAuthOptions> authOptions,
    ILogger<RepoWorkspaceManager> logger)
{
    private readonly ApplierOptions _options = applierOptions.Value;
    private readonly ApplierAuthOptions _auth = authOptions.Value;

    public string PrimaryClonePath(ApplierRepoOptions repo) =>
        RepoWorkspaceLayout.PrimaryClonePath(_options.WorkingDirectory, repo.Owner, repo.Name);

    public string BaselinePath(ApplierRepoOptions repo) =>
        RepoWorkspaceLayout.BaselinePath(_options.WorkingDirectory, repo.Owner, repo.Name);

    public string WorktreePath(ApplierRepoOptions repo, string ticketKey) =>
        RepoWorkspaceLayout.WorktreePath(_options.WorkingDirectory, repo.Owner, repo.Name, ticketKey);

    public async Task<string> EnsureCloneAsync(ApplierRepoOptions repo, CancellationToken ct)
    {
        string clonePath = PrimaryClonePath(repo);
        Directory.CreateDirectory(Path.GetDirectoryName(clonePath)!);

        if (Directory.Exists(Path.Combine(clonePath, ".git")))
        {
            logger.LogInformation("Updating clone for {Repo} at {Path}", repo.FullName, clonePath);
            GitProcessResult fetch = await git.RunAsync(clonePath, "fetch --all --prune", ct);
            if (!fetch.Success)
            {
                throw new InvalidOperationException($"git fetch failed for {repo.FullName}: {fetch.StdErr}");
            }
            GitProcessResult checkout = await git.RunAsync(clonePath, $"checkout {repo.PrimaryBranch}", ct);
            if (!checkout.Success)
            {
                throw new InvalidOperationException($"git checkout {repo.PrimaryBranch} failed for {repo.FullName}: {checkout.StdErr}");
            }
            GitProcessResult reset = await git.RunAsync(clonePath, $"reset --hard origin/{repo.PrimaryBranch}", ct);
            if (!reset.Success)
            {
                throw new InvalidOperationException($"git reset failed for {repo.FullName}: {reset.StdErr}");
            }
        }
        else
        {
            logger.LogInformation("Cloning {Repo} to {Path}", repo.FullName, clonePath);
            Directory.CreateDirectory(clonePath);
            string parentDir = Path.GetDirectoryName(clonePath)!;
            string cloneUrl = BuildCloneUrl(repo.FullName);
            string targetName = Path.GetFileName(clonePath);
            GitProcessResult clone = await git.RunAsync(parentDir, $"clone --branch {repo.PrimaryBranch} {cloneUrl} {targetName}", ct);
            if (!clone.Success)
            {
                throw new InvalidOperationException($"git clone failed for {repo.FullName}: {clone.StdErr}");
            }
        }

        return clonePath;
    }

    public async Task<BaselineRebuildResult> RebuildBaselineAsync(ApplierRepoOptions repo, CancellationToken ct)
    {
        string clonePath = await EnsureCloneAsync(repo, ct);
        string baselinePath = BaselinePath(repo);
        Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);

        GitProcessResult head = await git.RunAsync(clonePath, "rev-parse HEAD", ct);
        if (!head.Success)
        {
            throw new InvalidOperationException($"git rev-parse failed for {repo.FullName}: {head.StdErr}");
        }
        string commitSha = head.StdOut.Trim();

        IReadOnlyList<string> outputRoots = OutputRootResolver.GetEffectiveOutputRoots(repo);
        string firstRoot = outputRoots[0];
        string outputDirHint = Path.Combine(clonePath, ResolveFirstRootDir(firstRoot));

        BuildCommandContext context = new(
            WorkingDirectory: clonePath,
            PrimaryClonePath: clonePath,
            BaselineDir: baselinePath,
            OutputDir: outputDirHint,
            TicketKey: string.Empty,
            RepoOwner: repo.Owner,
            RepoName: repo.Name);

        BuildCommandResult build = await buildRunner.RunAsync(repo.BuildCommand, repo, context, ct);
        if (!build.Success)
        {
            throw new InvalidOperationException($"Baseline build failed for {repo.FullName} (exit {build.ExitCode}): {build.StdErr}");
        }

        if (Directory.Exists(baselinePath))
        {
            Directory.Delete(baselinePath, recursive: true);
        }
        Directory.CreateDirectory(baselinePath);

        IReadOnlyList<string> files = OutputRootResolver.ResolveFiles(clonePath, outputRoots);
        foreach (string relative in files)
        {
            string source = Path.Combine(clonePath, relative);
            string destination = Path.Combine(baselinePath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }

        DateTimeOffset builtAt = DateTimeOffset.UtcNow;
        await baselineStore.UpsertAsync(repo.FullName, commitSha, builtAt, ct);

        logger.LogInformation("Baseline rebuilt for {Repo} at sha={Sha} ({Count} files snapshotted)", repo.FullName, commitSha, files.Count);
        return new BaselineRebuildResult(commitSha, builtAt, files.Count);
    }

    public async Task<string> EnsureWorktreeAsync(ApplierRepoOptions repo, string ticketKey, CancellationToken ct)
    {
        string clonePath = PrimaryClonePath(repo);
        string baselinePath = BaselinePath(repo);
        string worktreePath = WorktreePath(repo, ticketKey);

        RepoBaselineRecord? baseline = await baselineStore.GetAsync(repo.FullName, ct)
            ?? throw new InvalidOperationException($"No baseline recorded for {repo.FullName}; rebuild baseline before provisioning a worktree.");

        if (Directory.Exists(worktreePath))
        {
            await git.RunAsync(clonePath, $"worktree remove --force \"{worktreePath}\"", ct);
            if (Directory.Exists(worktreePath))
            {
                Directory.Delete(worktreePath, recursive: true);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
        GitProcessResult add = await git.RunAsync(
            clonePath,
            $"worktree add -B {ticketKey} \"{worktreePath}\" {baseline.BaselineCommitSha}",
            ct);
        if (!add.Success)
        {
            throw new InvalidOperationException($"git worktree add failed for {repo.FullName} ticket={ticketKey}: {add.StdErr}");
        }

        if (Directory.Exists(baselinePath))
        {
            CopyDirectory(baselinePath, worktreePath);
        }

        return baseline.BaselineCommitSha;
    }

    public async Task RemoveWorktreeAsync(ApplierRepoOptions repo, string ticketKey, CancellationToken ct)
    {
        string clonePath = PrimaryClonePath(repo);
        string worktreePath = WorktreePath(repo, ticketKey);
        if (Directory.Exists(worktreePath))
        {
            await git.RunAsync(clonePath, $"worktree remove --force \"{worktreePath}\"", ct);
            if (Directory.Exists(worktreePath))
            {
                Directory.Delete(worktreePath, recursive: true);
            }
        }
    }

    private string BuildCloneUrl(string repoFullName)
    {
        string? token = _auth.ResolveToken();
        if (!string.IsNullOrEmpty(token))
        {
            return $"https://x-access-token:{token}@github.com/{repoFullName}.git";
        }
        return $"https://github.com/{repoFullName}.git";
    }

    private static string ResolveFirstRootDir(string pattern)
    {
        string normalized = pattern.Replace('\\', '/').TrimStart('.', '/');
        int wildcard = normalized.IndexOfAny(['*', '?', '[']);
        string dirPart = wildcard >= 0 ? normalized[..wildcard] : normalized;
        dirPart = dirPart.TrimEnd('/');
        return string.IsNullOrEmpty(dirPart) ? "." : dirPart;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
