using System.Diagnostics;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;

/// <summary>
/// Thin wrapper around invoking <c>git</c> as a child process, modelled on
/// <c>GitHubCommitFileExtractor.RunGitAsync</c>. Uses an argument list (no shell
/// quoting) and captures stdout/stderr.
/// </summary>
public static class GitRunner
{
    /// <summary>The result of a git invocation that is allowed to fail.</summary>
    public readonly record struct GitResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>Runs git and returns stdout; throws when the exit code is non-zero.</summary>
    public static async Task<string> RunAsync(
        string workingDir,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default)
    {
        GitResult result = await TryRunAsync(workingDir, arguments, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {result.ExitCode}: {result.StdErr}");
        }
        return result.StdOut;
    }

    /// <summary>Runs git and returns the exit code + output without throwing on failure.</summary>
    public static async Task<GitResult> TryRunAsync(
        string workingDir,
        IReadOnlyList<string> arguments,
        CancellationToken ct = default)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        return new GitResult(process.ExitCode, stdout, stderr);
    }
}
