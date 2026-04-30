namespace FhirAugury.Processor.Jira.Fhir.Applier.Workspace;

/// <summary>
/// Result of running a git command. Non-zero exit codes are returned, not thrown,
/// so callers can decide whether to log-and-continue or fail.
/// </summary>
public sealed record GitProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Abstraction over invoking <c>git</c> for testability. Production implementation
/// shells out to the local <c>git</c> binary; tests substitute a fake.
/// </summary>
public interface IGitProcessRunner
{
    Task<GitProcessResult> RunAsync(string workingDirectory, string arguments, CancellationToken ct);
}
