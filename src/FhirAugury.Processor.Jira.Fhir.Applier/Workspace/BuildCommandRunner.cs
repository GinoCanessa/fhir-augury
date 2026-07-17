using System.Diagnostics;
using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Workspace;

/// <summary>
/// Token map passed to <see cref="BuildCommandRunner"/>. <c>TicketKey</c> is empty for
/// baseline builds and the issue key for per-ticket builds; the build command author
/// is free to ignore tokens it doesn't need.
/// </summary>
public sealed record BuildCommandContext(
    string WorkingDirectory,
    string PrimaryClonePath,
    string BaselineDir,
    string OutputDir,
    string TicketKey,
    string RepoOwner,
    string RepoName);

public sealed record BuildCommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Runs a per-repo <c>BuildCommand</c> template after substituting workspace tokens.
/// Splits the rendered command on the first whitespace into <c>(fileName, arguments)</c>
/// and invokes via <see cref="ProcessStartInfo"/> without a shell wrapper, so users
/// who need shell features supply <c>bash -lc "..."</c> or <c>cmd /c "..."</c> in
/// <c>BuildCommand</c> themselves.
/// </summary>
public sealed class BuildCommandRunner(ILogger<BuildCommandRunner> logger)
{
    public string RenderCommand(string template, BuildCommandContext context)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new ArgumentException("BuildCommand template must be non-empty.", nameof(template));
        }

        return template
            .Replace("{worktreePath}", context.WorkingDirectory)
            .Replace("{primaryClonePath}", context.PrimaryClonePath)
            .Replace("{baselineDir}", context.BaselineDir)
            .Replace("{outputDir}", context.OutputDir)
            .Replace("{ticketKey}", context.TicketKey)
            .Replace("{repoOwner}", context.RepoOwner)
            .Replace("{repoName}", context.RepoName);
    }

    public async Task<BuildCommandResult> RunAsync(
        string template,
        ApplierRepoOptions repo,
        BuildCommandContext context,
        CancellationToken ct)
    {
        string rendered = RenderCommand(template, context);
        (string fileName, string arguments) = SplitCommand(rendered);

        ProcessStartInfo psi = new()
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = context.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        logger.LogInformation("Running build for {Repo} ticket={Ticket}: {Cmd}", repo.FullName, context.TicketKey, rendered);

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start build process: {fileName}");
        string stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            logger.LogWarning("Build failed for {Repo} ticket={Ticket} (exit {Code}): {Stderr}", repo.FullName, context.TicketKey, process.ExitCode, stderr);
        }
        return new BuildCommandResult(process.ExitCode, stdout, stderr);
    }

    private static (string FileName, string Arguments) SplitCommand(string rendered)
    {
        string trimmed = rendered.Trim();
        int firstSpace = trimmed.IndexOf(' ');
        if (firstSpace < 0)
        {
            return (trimmed, string.Empty);
        }
        return (trimmed[..firstSpace], trimmed[(firstSpace + 1)..]);
    }
}
