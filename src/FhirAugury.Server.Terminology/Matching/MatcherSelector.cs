namespace FhirAugury.Server.Terminology.Matching;

/// <summary>
/// Resolves an <see cref="ITerminologyMatcher"/> by mode string. The
/// selector enumerates all registered matchers from DI and looks up
/// by <see cref="ITerminologyMatcher.Mode"/> case-insensitively.
/// </summary>
public sealed class MatcherSelector
{
    private readonly Dictionary<string, ITerminologyMatcher> _byMode;

    public MatcherSelector(IEnumerable<ITerminologyMatcher> matchers)
    {
        _byMode = new Dictionary<string, ITerminologyMatcher>(StringComparer.OrdinalIgnoreCase);
        foreach (ITerminologyMatcher m in matchers)
        {
            _byMode[m.Mode] = m;
        }
    }

    public IReadOnlyCollection<string> AvailableModes => _byMode.Keys;

    public bool TryResolve(string mode, out ITerminologyMatcher matcher)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            matcher = null!;
            return false;
        }

        return _byMode.TryGetValue(mode.Trim(), out matcher!);
    }
}
