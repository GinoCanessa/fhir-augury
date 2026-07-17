using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.WorkGroups;

/// <summary>
/// Resolves a free-form work-group selector (any of <c>code</c>,
/// <c>nameClean</c>, or <c>name</c>) to a canonical
/// <see cref="Hl7WorkGroupDto"/> against a snapshot of the
/// <c>hl7_workgroups</c> catalog.
/// </summary>
/// <remarks>
/// <para>
/// This type is the single first-party owner of work-group selector
/// translation. It deliberately accepts every historical form (including
/// the legacy preparer slug variant produced by
/// <c>REPLACE(name, ' ', '')</c>) so callers do not silently break during
/// the rollout that unifies storage on
/// <see cref="Hl7WorkGroupNameCleaner.Clean(string?)"/>.
/// </para>
/// <para>
/// Resolution order, on a case-folded input:
/// <list type="number">
///   <item>Exact <see cref="Hl7WorkGroupDto.Code"/> match.</item>
///   <item>Exact <see cref="Hl7WorkGroupDto.NameClean"/> match.</item>
///   <item>Exact <see cref="Hl7WorkGroupDto.Name"/> match (also the legacy
///         preparer slug variant <c>REPLACE(name, ' ', '')</c>).</item>
///   <item>Normalized-name match: <see cref="Hl7WorkGroupNameCleaner.Clean(string?)"/>
///         on both sides.</item>
///   <item>Fuzzy <see cref="Hl7WorkGroupDto.Name"/> match using
///         <see cref="JaroWinkler.Compute(ReadOnlySpan{char}, ReadOnlySpan{char})"/>.
///         If the best score clears
///         <see cref="WorkGroupResolverOptions.SimilarityThreshold"/> and the
///         runner-up is more than
///         <see cref="WorkGroupResolverOptions.AmbiguityDelta"/> away, the
///         result is <see cref="WorkGroupResolveOutcome.Found"/>; if a
///         runner-up is within the delta the result is
///         <see cref="WorkGroupResolveOutcome.Ambiguous"/>.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class WorkGroupResolver
{
    private readonly IReadOnlyList<Hl7WorkGroupDto> _snapshot;
    private readonly WorkGroupResolverOptions _options;
    private readonly ILogger<WorkGroupResolver>? _logger;

    private readonly Dictionary<string, Hl7WorkGroupDto> _byCode;
    private readonly Dictionary<string, Hl7WorkGroupDto> _byNameClean;
    private readonly Dictionary<string, Hl7WorkGroupDto> _byName;
    private readonly Dictionary<string, Hl7WorkGroupDto> _byLegacyPreparerSlug;
    private readonly Dictionary<string, Hl7WorkGroupDto> _byNormalizedName;

    public WorkGroupResolver(
        IReadOnlyList<Hl7WorkGroupDto> snapshot,
        WorkGroupResolverOptions? options = null,
        ILogger<WorkGroupResolver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _options = options ?? new WorkGroupResolverOptions();
        _logger = logger;

        _byCode = new Dictionary<string, Hl7WorkGroupDto>(StringComparer.OrdinalIgnoreCase);
        _byNameClean = new Dictionary<string, Hl7WorkGroupDto>(StringComparer.OrdinalIgnoreCase);
        _byName = new Dictionary<string, Hl7WorkGroupDto>(StringComparer.OrdinalIgnoreCase);
        _byLegacyPreparerSlug = new Dictionary<string, Hl7WorkGroupDto>(StringComparer.OrdinalIgnoreCase);
        _byNormalizedName = new Dictionary<string, Hl7WorkGroupDto>(StringComparer.OrdinalIgnoreCase);

        bool degraded = snapshot.Count == 0;
        foreach (Hl7WorkGroupDto dto in snapshot)
        {
            if (string.IsNullOrWhiteSpace(dto.Code)) degraded = true;
            if (!string.IsNullOrWhiteSpace(dto.Code))
                _byCode.TryAdd(dto.Code, dto);
            if (!string.IsNullOrWhiteSpace(dto.NameClean))
                _byNameClean.TryAdd(dto.NameClean, dto);
            if (!string.IsNullOrWhiteSpace(dto.Name))
            {
                _byName.TryAdd(dto.Name, dto);
                _byLegacyPreparerSlug.TryAdd(dto.Name.Replace(" ", string.Empty), dto);
                string normalized = Hl7WorkGroupNameCleaner.Clean(dto.Name);
                if (!string.IsNullOrEmpty(normalized))
                    _byNormalizedName.TryAdd(normalized, dto);
            }
        }
        CatalogJoinDegraded = degraded;
    }

    /// <summary>The snapshot used to build this resolver. Read-only.</summary>
    public IReadOnlyList<Hl7WorkGroupDto> Snapshot => _snapshot;

    /// <summary>
    /// <c>true</c> when the snapshot was empty or any row had a missing
    /// <see cref="Hl7WorkGroupDto.Code"/>. Surfaced by callers (e.g. the
    /// <c>list-jira-workgroups</c> endpoint envelope) so orchestrators can
    /// proceed-with-warning per D4.
    /// </summary>
    public bool CatalogJoinDegraded { get; }

    /// <summary>
    /// Resolves <paramref name="input"/> against the snapshot. Never throws;
    /// returns <see cref="WorkGroupResolveOutcome.NotFound"/> for null /
    /// whitespace input.
    /// </summary>
    public WorkGroupResolveResult Resolve(string? input)
    {
        string raw = input ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Log(new WorkGroupResolveResult(
                WorkGroupResolveOutcome.NotFound,
                Match: null,
                Candidates: [],
                Input: raw,
                MatchKind: WorkGroupResolveMatchKind.None,
                Score: null));
        }

        string trimmed = raw.Trim();

        if (_byCode.TryGetValue(trimmed, out Hl7WorkGroupDto? match))
            return Log(Exact(match, trimmed, WorkGroupResolveMatchKind.ExactCode));
        if (_byNameClean.TryGetValue(trimmed, out match))
            return Log(Exact(match, trimmed, WorkGroupResolveMatchKind.ExactNameClean));
        if (_byName.TryGetValue(trimmed, out match))
            return Log(Exact(match, trimmed, WorkGroupResolveMatchKind.ExactName));
        if (_byLegacyPreparerSlug.TryGetValue(trimmed, out match))
            return Log(Exact(match, trimmed, WorkGroupResolveMatchKind.ExactName));

        string normalized = Hl7WorkGroupNameCleaner.Clean(trimmed);
        if (!string.IsNullOrEmpty(normalized) &&
            _byNormalizedName.TryGetValue(normalized, out match))
        {
            if (_options.IncludeRetired || !match.Retired)
            {
                return Log(new WorkGroupResolveResult(
                    WorkGroupResolveOutcome.Found,
                    Match: match,
                    Candidates: [],
                    Input: trimmed,
                    MatchKind: WorkGroupResolveMatchKind.NormalizedName,
                    Score: 1.0));
            }
        }

        List<WorkGroupResolveCandidate> scored = new List<WorkGroupResolveCandidate>(_snapshot.Count);
        foreach (Hl7WorkGroupDto dto in _snapshot)
        {
            if (!_options.IncludeRetired && dto.Retired) continue;
            if (string.IsNullOrWhiteSpace(dto.Name)) continue;
            double score = JaroWinkler.Compute(trimmed, dto.Name);
            scored.Add(new WorkGroupResolveCandidate(dto, score));
        }
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        if (scored.Count == 0)
        {
            return Log(new WorkGroupResolveResult(
                WorkGroupResolveOutcome.NotFound,
                Match: null,
                Candidates: [],
                Input: trimmed,
                MatchKind: WorkGroupResolveMatchKind.None,
                Score: null));
        }

        WorkGroupResolveCandidate top = scored[0];
        WorkGroupResolveCandidate? second = scored.Count > 1 ? scored[1] : null;

        if (top.Score >= _options.SimilarityThreshold)
        {
            if (second is not null && (top.Score - second.Score) < _options.AmbiguityDelta)
            {
                List<WorkGroupResolveCandidate> tied = new List<WorkGroupResolveCandidate>();
                foreach (WorkGroupResolveCandidate c in scored)
                {
                    if ((top.Score - c.Score) < _options.AmbiguityDelta) tied.Add(c);
                    else break;
                }
                return Log(new WorkGroupResolveResult(
                    WorkGroupResolveOutcome.Ambiguous,
                    Match: null,
                    Candidates: tied,
                    Input: trimmed,
                    MatchKind: WorkGroupResolveMatchKind.None,
                    Score: top.Score));
            }

            return Log(new WorkGroupResolveResult(
                WorkGroupResolveOutcome.Found,
                Match: top.Dto,
                Candidates: [],
                Input: trimmed,
                MatchKind: WorkGroupResolveMatchKind.FuzzyName,
                Score: top.Score));
        }

        int suggestCount = Math.Min(3, scored.Count);
        List<WorkGroupResolveCandidate> suggestions = new List<WorkGroupResolveCandidate>(suggestCount);
        for (int i = 0; i < suggestCount; i++) suggestions.Add(scored[i]);

        return Log(new WorkGroupResolveResult(
            WorkGroupResolveOutcome.NotFound,
            Match: null,
            Candidates: suggestions,
            Input: trimmed,
            MatchKind: WorkGroupResolveMatchKind.None,
            Score: null));
    }

    private static WorkGroupResolveResult Exact(
        Hl7WorkGroupDto match,
        string input,
        WorkGroupResolveMatchKind kind) =>
        new WorkGroupResolveResult(
            WorkGroupResolveOutcome.Found,
            Match: match,
            Candidates: [],
            Input: input,
            MatchKind: kind,
            Score: null);

    private WorkGroupResolveResult Log(WorkGroupResolveResult result)
    {
        if (_logger is null) return result;

        LogLevel level = result.MatchKind is
            WorkGroupResolveMatchKind.FuzzyName or
            WorkGroupResolveMatchKind.NormalizedName
            ? LogLevel.Information
            : LogLevel.Debug;
        if (result.Outcome is WorkGroupResolveOutcome.Ambiguous or WorkGroupResolveOutcome.NotFound)
            level = LogLevel.Information;

        _logger.Log(
            level,
            "WorkGroupResolver: input={Input} outcome={Outcome} matchKind={MatchKind} matchCode={MatchCode} score={Score} candidates={CandidateCount}",
            result.Input,
            result.Outcome,
            result.MatchKind,
            result.Match?.Code,
            result.Score,
            result.Candidates.Count);

        return result;
    }
}
