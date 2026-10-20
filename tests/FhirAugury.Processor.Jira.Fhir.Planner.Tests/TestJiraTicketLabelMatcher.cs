using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

internal sealed class TestJiraTicketLabelMatcher : IJiraTicketLabelMatcher
{
    private readonly Queue<Func<IReadOnlyList<string>, ResolvedJiraProcessingFilters,
        CancellationToken, Task<IReadOnlyList<string>>>> _responses = new();

    public List<(IReadOnlyList<string> Keys, ResolvedJiraProcessingFilters Filters,
        CancellationToken Token)> Calls { get; } = [];

    public void Enqueue(IReadOnlyList<string> matches)
        => Enqueue((_, _, _) => Task.FromResult(matches));

    public void Enqueue(
        Func<IReadOnlyList<string>, ResolvedJiraProcessingFilters,
            CancellationToken, Task<IReadOnlyList<string>>> response)
        => _responses.Enqueue(response);

    public Task<IReadOnlyList<string>> MatchKeysAsync(
        IReadOnlyList<string> keys,
        ResolvedJiraProcessingFilters filters,
        CancellationToken ct)
    {
        Calls.Add((keys.ToArray(), filters, ct));
        if (!_responses.TryDequeue(out var response))
        {
            throw new InvalidOperationException("Unexpected Jira label matching call.");
        }
        return response(keys, filters, ct);
    }
}
