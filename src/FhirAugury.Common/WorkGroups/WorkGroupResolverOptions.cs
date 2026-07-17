namespace FhirAugury.Common.WorkGroups;

/// <summary>
/// Tunables for <see cref="WorkGroupResolver"/>. Defaults match decision D7
/// in the source feature request: conservative fuzzy threshold, conservative
/// ambiguity delta, retired rows excluded.
/// </summary>
/// <param name="SimilarityThreshold">Minimum Jaro-Winkler score (case-folded)
///   a candidate name must clear to be considered a fuzzy match. Default
///   <c>0.92</c>.</param>
/// <param name="AmbiguityDelta">Maximum allowed score gap between the
///   top candidate and the runner-up. Closer than this and the resolver
///   returns <see cref="WorkGroupResolveOutcome.Ambiguous"/> instead of
///   silently picking. Default <c>0.05</c>.</param>
/// <param name="IncludeRetired">When <c>false</c> (the default) retired
///   work-group rows are skipped during fuzzy / normalized matching. Exact
///   matches still hit retired rows so callers can still address historical
///   ids deliberately.</param>
public sealed record WorkGroupResolverOptions(
    double SimilarityThreshold = 0.92,
    double AmbiguityDelta = 0.05,
    bool IncludeRetired = false);
