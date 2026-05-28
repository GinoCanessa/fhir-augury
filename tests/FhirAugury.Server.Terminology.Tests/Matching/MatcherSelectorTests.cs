using FhirAugury.Server.Terminology.Matching;
using FhirAugury.Server.Terminology.Models;

namespace FhirAugury.Server.Terminology.Tests.Matching;

public class MatcherSelectorTests
{
    [Fact]
    public void TryResolve_FindsLexicalMatcher_CaseInsensitive()
    {
        MatcherSelector selector = new([new StubMatcher("lexical")]);

        Assert.True(selector.TryResolve("LEXICAL", out ITerminologyMatcher? m));
        Assert.Equal("lexical", m.Mode);
    }

    [Fact]
    public void TryResolve_ReturnsFalse_ForUnknownMode()
    {
        MatcherSelector selector = new([new StubMatcher("lexical")]);

        Assert.False(selector.TryResolve("hybrid", out ITerminologyMatcher? _));
    }

    [Fact]
    public void TryResolve_ReturnsFalse_ForEmptyMode()
    {
        MatcherSelector selector = new([new StubMatcher("lexical")]);

        Assert.False(selector.TryResolve("", out ITerminologyMatcher? _));
        Assert.False(selector.TryResolve("   ", out ITerminologyMatcher? _));
    }

    [Fact]
    public void AvailableModes_ReflectsRegisteredMatchers()
    {
        MatcherSelector selector = new([new StubMatcher("lexical"), new StubMatcher("hybrid")]);

        Assert.Contains("lexical", selector.AvailableModes);
        Assert.Contains("hybrid", selector.AvailableModes);
    }

    private sealed class StubMatcher(string mode) : ITerminologyMatcher
    {
        public string Mode => mode;

        public Task<IReadOnlyList<OverlapCandidate>> MatchAsync(
            NormalizedSubmission submission, OverlapCheckRequest request, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<OverlapCandidate>>([]);
        }
    }
}
