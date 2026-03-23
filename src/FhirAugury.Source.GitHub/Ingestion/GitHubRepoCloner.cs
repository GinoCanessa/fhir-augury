using System.Diagnostics;
using FhirAugury.Source.GitHub.Cache;
using FhirAugury.Source.GitHub.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Manages local git clones of tracked repositories under the cache directory.
/// Clones on first run, fetches and merges for incremental updates.
/// </summary>
public class GitHubRepoCloner(IOptions<GitHubServiceOptions> optionsAccessor, ILogger<GitHubRepoCloner> logger)
{
    private readonly GitHubServiceOptions _options = optionsAccessor.Value;
    /// <summary>
    /// Ensures a local clone exists for the given repository, creating or updating as needed.
    /// Returns the path to the local clone directory.
    /// </summary>
    public async Task<string> EnsureCloneAsync(string repoFullName, CancellationToken ct = default)
    {
        var safeName = repoFullName.Replace('/', '_');
        var cloneDir = Path.GetFullPath(
            Path.Combine(_options.CachePath, GitHubCacheLayout.ReposSubDir, safeName, GitHubCacheLayout.CloneSubDir));

        if (Directory.Exists(Path.Combine(cloneDir, ".git")))
        {
            logger.LogInformation("Updating existing clone for {Repo}", repoFullName);
            await RunGitAsync(cloneDir, "fetch --all --prune", ct);
            await RunGitAsync(cloneDir, "merge --ff-only FETCH_HEAD", ct);
        }
        else
        {
            logger.LogInformation("Cloning {Repo} to {Path}", repoFullName, cloneDir);
            Directory.CreateDirectory(cloneDir);

            var cloneUrl = BuildCloneUrl(repoFullName);
            var parentDir = Path.GetDirectoryName(cloneDir)!;
            await RunGitAsync(parentDir, $"clone {cloneUrl} {GitHubCacheLayout.CloneSubDir}", ct);
        }

        return cloneDir;
    }

    /// <summary>Gets the path where a repo's clone would be stored.</summary>
    public string GetClonePath(string repoFullName)
    {
        var safeName = repoFullName.Replace('/', '_');
        return Path.GetFullPath(
            Path.Combine(_options.CachePath, GitHubCacheLayout.ReposSubDir, safeName, GitHubCacheLayout.CloneSubDir));
    }

    private string BuildCloneUrl(string repoFullName)
    {
        var token = _options.Auth.ResolveToken();
        if (!string.IsNullOrEmpty(token))
            return $"https://x-access-token:{token}@github.com/{repoFullName}.git";

        return $"https://github.com/{repoFullName}.git";
    }

    private async Task RunGitAsync(string workingDir, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            logger.LogWarning("git {Args} failed (exit {Code}): {Stderr}", arguments, process.ExitCode, stderr);
        }
        else if (!string.IsNullOrWhiteSpace(stdout))
        {
            logger.LogDebug("git {Args}: {Output}", arguments, stdout.Trim());
        }
    }
}
