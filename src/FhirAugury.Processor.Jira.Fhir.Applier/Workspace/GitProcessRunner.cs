using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Workspace;

public sealed class GitProcessRunner(ILogger<GitProcessRunner> logger) : IGitProcessRunner
{
    public async Task<GitProcessResult> RunAsync(string workingDirectory, string arguments, CancellationToken ct)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");
        string stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            logger.LogWarning("git {Args} (in {Cwd}) failed (exit {Code}): {Stderr}", arguments, workingDirectory, process.ExitCode, stderr);
        }
        else if (!string.IsNullOrWhiteSpace(stdout))
        {
            logger.LogDebug("git {Args}: {Output}", arguments, stdout.Trim());
        }

        return new GitProcessResult(process.ExitCode, stdout, stderr);
    }
}
