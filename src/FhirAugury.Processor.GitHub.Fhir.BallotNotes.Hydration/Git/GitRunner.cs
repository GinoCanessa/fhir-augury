using System.Diagnostics;
using System.Text;

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

    /// <summary>
    /// Runs git feeding <paramref name="stdinLines"/> to its standard input
    /// (one line per entry, always <c>\n</c>-terminated) and returns the raw
    /// stdout <b>bytes</b>. Used for <c>git cat-file --batch</c>, whose output is
    /// length-delimited and may be binary, so it must not be decoded line by line.
    /// The stdout/stderr drains start <b>before</b> stdin is written to avoid a
    /// pipe-buffer deadlock on large output. Throws on a non-zero exit code.
    /// </summary>
    public static async Task<byte[]> RunWithInputAsync(
        string workingDir,
        IReadOnlyList<string> arguments,
        IEnumerable<string> stdinLines,
        CancellationToken ct = default)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
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

        // Start draining stdout (bytes) and stderr (text) BEFORE writing stdin so
        // a large response cannot fill the OS pipe buffer and deadlock the writer.
        Task<byte[]> stdoutTask = ReadToEndBytesAsync(process.StandardOutput.BaseStream, ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            Stream stdin = process.StandardInput.BaseStream;
            foreach (string line in stdinLines)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                await stdin.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
            await stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        byte[] stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {stderr}");
        }

        return stdout;
    }

    private static async Task<byte[]> ReadToEndBytesAsync(Stream stream, CancellationToken ct)
    {
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
