using FhirAugury.Server.Terminology.Models;

namespace FhirAugury.Server.Terminology.Matching;

/// <summary>
/// Pluggable scorer over the THO index. Implementations:
/// <see cref="LexicalMatcher"/> (Phase 3, default),
/// <c>EmbeddingMatcher</c> (Phase 5),
/// <c>HybridMatcher</c> (Phase 5).
/// </summary>
public interface ITerminologyMatcher
{
    /// <summary>The name this matcher is registered under (e.g. "lexical").</summary>
    string Mode { get; }

    Task<IReadOnlyList<OverlapCandidate>> MatchAsync(
        NormalizedSubmission submission,
        OverlapCheckRequest request,
        CancellationToken ct);
}
