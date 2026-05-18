namespace FhirAugury.Common.WorkGroups;

/// <summary>
/// Outcome of a <see cref="WorkGroupResolver.Resolve(string?)"/> call.
/// </summary>
public enum WorkGroupResolveOutcome
{
    /// <summary>A unique row matched.</summary>
    Found,

    /// <summary>No row matched and no fuzzy candidate cleared the threshold.</summary>
    NotFound,

    /// <summary>
    /// Two or more fuzzy candidates scored above the similarity threshold
    /// within <see cref="WorkGroupResolverOptions.AmbiguityDelta"/> of each
    /// other; the resolver refuses to pick.
    /// </summary>
    Ambiguous,
}

/// <summary>
/// Which rule produced the match. Useful for telemetry and for tests that
/// want to assert the resolver did not silently fall through to a weaker
/// rule.
/// </summary>
public enum WorkGroupResolveMatchKind
{
    None,
    ExactCode,
    ExactNameClean,
    ExactName,
    NormalizedName,
    FuzzyName,
}

/// <summary>
/// A candidate row plus its Jaro-Winkler score against the input. Used both
/// for the chosen result and for the "did you mean" payload returned on
/// <see cref="WorkGroupResolveOutcome.NotFound"/> and
/// <see cref="WorkGroupResolveOutcome.Ambiguous"/>.
/// </summary>
public sealed record WorkGroupResolveCandidate(Hl7WorkGroupDto Dto, double Score);

/// <summary>
/// Result returned by <see cref="WorkGroupResolver.Resolve(string?)"/>.
/// </summary>
/// <param name="Outcome">Whether a match was found, none was found, or two
///   or more candidates tied within the ambiguity delta.</param>
/// <param name="Match">The selected DTO when <see cref="Outcome"/> is
///   <see cref="WorkGroupResolveOutcome.Found"/>; <c>null</c> otherwise.</param>
/// <param name="Candidates">Top scoring runners-up. For <c>Found</c> via an
///   exact match this is empty; for <c>NotFound</c> it carries the top three
///   fuzzy candidates as a "did you mean" payload; for <c>Ambiguous</c> it
///   carries the tied candidates.</param>
/// <param name="Input">The original (case-preserving) input string.</param>
/// <param name="MatchKind">Which resolution rule produced <see cref="Match"/>;
///   <see cref="WorkGroupResolveMatchKind.None"/> when not Found.</param>
/// <param name="Score">For fuzzy / normalized matches, the Jaro-Winkler
///   score of <see cref="Match"/>. Null for exact matches.</param>
public sealed record WorkGroupResolveResult(
    WorkGroupResolveOutcome Outcome,
    Hl7WorkGroupDto? Match,
    IReadOnlyList<WorkGroupResolveCandidate> Candidates,
    string Input,
    WorkGroupResolveMatchKind MatchKind,
    double? Score);
