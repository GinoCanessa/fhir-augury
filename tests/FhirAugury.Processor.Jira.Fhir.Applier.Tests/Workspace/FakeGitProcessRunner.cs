using System.Collections.Concurrent;
using FhirAugury.Processor.Jira.Fhir.Applier.Workspace;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Workspace;

/// <summary>
/// In-memory <see cref="IGitProcessRunner"/> that records every invocation and answers
/// each <c>(workingDir, arguments)</c> with a configurable <see cref="GitProcessResult"/>.
/// </summary>
public sealed class FakeGitProcessRunner : IGitProcessRunner
{
    private readonly ConcurrentQueue<(string WorkingDir, string Arguments)> _calls = new();
    private readonly Dictionary<string, Func<GitProcessResult>> _responses = new(StringComparer.Ordinal);
    private GitProcessResult _default = new(0, string.Empty, string.Empty);

    public IReadOnlyList<(string WorkingDir, string Arguments)> Calls => [.. _calls];

    public void SetDefault(GitProcessResult result) => _default = result;

    public void Respond(string argumentsPrefix, GitProcessResult result) =>
        _responses[argumentsPrefix] = () => result;

    public void Respond(string argumentsPrefix, Func<GitProcessResult> factory) =>
        _responses[argumentsPrefix] = factory;

    public Task<GitProcessResult> RunAsync(string workingDirectory, string arguments, CancellationToken ct)
    {
        _calls.Enqueue((workingDirectory, arguments));
        foreach ((string prefix, Func<GitProcessResult> factory) in _responses)
        {
            if (arguments.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Task.FromResult(factory());
            }
        }
        return Task.FromResult(_default);
    }
}
