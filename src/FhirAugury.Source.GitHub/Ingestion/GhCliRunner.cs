using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FhirAugury.Source.GitHub.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Invokes the <c>gh</c> CLI as an external process, captures output, and parses JSON results.
/// Handles timeouts, error detection, and authentication validation.
/// </summary>
public class GhCliRunner(
    IOptions<GitHubServiceOptions> optionsAccessor,
    ILogger<GhCliRunner> logger)
{
    private readonly GhCliConfiguration _config = optionsAccessor.Value.GhCli;
    private readonly SemaphoreSlim _processGate = new(
        optionsAccessor.Value.GhCli.MaxConcurrentProcesses,
        optionsAccessor.Value.GhCli.MaxConcurrentProcesses);

    /// <summary>Runs a gh command and returns the parsed JSON output.</summary>
    public async Task<JsonDocument> RunAsync(string arguments, CancellationToken ct)
    {
        (string stdout, string stderr, int exitCode) = await ExecuteProcessAsync(arguments, ct);

        if (exitCode != 0)
        {
            logger.LogDebug("gh command failed (exit {ExitCode}): gh {Args}\nstderr: {Stderr}", exitCode, arguments, stderr);
            throw new InvalidOperationException($"gh command failed (exit {exitCode}): {stderr}");
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            // Return empty array for commands that produce no output
            return JsonDocument.Parse("[]");
        }

        try
        {
            return JsonDocument.Parse(stdout);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse gh JSON output for: gh {Args}", arguments);
            throw new InvalidOperationException($"Invalid JSON from gh command: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Runs a gh command that returns a JSON array and yields each element.
    /// Useful for processing large result sets without buffering the entire array.
    /// </summary>
    public async IAsyncEnumerable<JsonElement> StreamArrayAsync(
        string arguments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using JsonDocument doc = await RunAsync(arguments, ct);

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in doc.RootElement.EnumerateArray())
            {
                yield return element.Clone();
            }
        }
        else
        {
            // Single object — yield it directly
            yield return doc.RootElement.Clone();
        }
    }

    /// <summary>
    /// Runs a gh command that uses <c>gh api --paginate</c>, which outputs
    /// concatenated JSON arrays (one per page). Parses and yields all elements.
    /// </summary>
    public async IAsyncEnumerable<JsonElement> StreamPaginatedApiAsync(
        string apiPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string hostnameArg = !string.IsNullOrEmpty(_config.Hostname) ? $" --hostname {_config.Hostname}" : "";
        string arguments = $"api --paginate \"{apiPath}\"{hostnameArg}";

        (string stdout, string stderr, int exitCode) = await ExecuteProcessAsync(arguments, ct);

        if (exitCode != 0)
        {
            logger.LogError("gh api failed (exit {ExitCode}): {Stderr}", exitCode, stderr);
            throw new InvalidOperationException($"gh api failed (exit {exitCode}): {stderr}");
        }

        if (string.IsNullOrWhiteSpace(stdout))
            yield break;

        // gh api --paginate concatenates JSON arrays: [...][...][...]
        // Convert to bytes for Utf8JsonReader-based parsing
        List<JsonElement> elements = ParseConcatenatedJsonArrays(stdout);

        foreach (JsonElement element in elements)
        {
            yield return element;
        }
    }

    /// <summary>
    /// Parses concatenated JSON arrays (e.g., from gh api --paginate) and returns all elements.
    /// </summary>
    private List<JsonElement> ParseConcatenatedJsonArrays(string json)
    {
        List<JsonElement> elements = [];
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        int position = 0;

        while (position < bytes.Length)
        {
            // Skip whitespace between arrays
            while (position < bytes.Length && (bytes[position] == (byte)' ' || bytes[position] == (byte)'\n' || bytes[position] == (byte)'\r' || bytes[position] == (byte)'\t'))
                position++;

            if (position >= bytes.Length) break;

            try
            {
                Utf8JsonReader reader = new Utf8JsonReader(bytes.AsSpan(position));
                if (!JsonDocument.TryParseValue(ref reader, out JsonDocument? doc))
                    break;

                using (doc)
                {
                    int bytesConsumed = (int)reader.BytesConsumed;
                    position += bytesConsumed;

                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement element in doc.RootElement.EnumerateArray())
                        {
                            elements.Add(element.Clone());
                        }
                    }
                    else
                    {
                        elements.Add(doc.RootElement.Clone());
                    }
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to parse paginated API response chunk at position {Position}", position);
                break;
            }
        }

        return elements;
    }

    /// <summary>Checks if gh is installed and authenticated. Logs status details.</summary>
    public async Task<bool> ValidateAsync(CancellationToken ct)
    {
        try
        {
            (string stdout, string stderr, int exitCode) = await ExecuteProcessAsync("auth status", ct);

            if (exitCode != 0)
            {
                logger.LogError("gh is not authenticated. Run 'gh auth login' to authenticate.\nstderr: {Stderr}", stderr);
                return false;
            }

            logger.LogInformation("gh CLI authenticated: {Status}", stdout.Trim());
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "gh CLI is not available. Ensure 'gh' is installed and on PATH.");
            return false;
        }
    }

    /// <summary>Builds common arguments for repo-scoped gh commands.</summary>
    public string BuildRepoArgs(string repoFullName)
    {
        string hostnameArg = !string.IsNullOrEmpty(_config.Hostname) ? $" --hostname {_config.Hostname}" : "";
        return $"--repo {repoFullName}{hostnameArg}";
    }

    private async Task<(string Stdout, string Stderr, int ExitCode)> ExecuteProcessAsync(
        string arguments, CancellationToken ct)
    {
        if (_processGate.CurrentCount == 0)
            logger.LogDebug("Waiting for previous gh CLI operation to complete before running: gh {Args}", arguments);

        await _processGate.WaitAsync(ct);
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = _config.ExecutablePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            logger.LogDebug("Running: {Exe} {Args}", _config.ExecutablePath, arguments);

            using Process process = new Process { StartInfo = psi };
            StringBuilder stdoutBuilder = new StringBuilder();
            StringBuilder stderrBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            TimeSpan timeout = _config.GetProcessTimeout();
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout — kill the process
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                throw new TimeoutException($"gh command timed out after {timeout}: gh {arguments}");
            }

            return (stdoutBuilder.ToString(), stderrBuilder.ToString(), process.ExitCode);
        }
        finally
        {
            _processGate.Release();
        }
    }
}
