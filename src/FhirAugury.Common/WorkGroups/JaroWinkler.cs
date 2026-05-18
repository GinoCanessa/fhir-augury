namespace FhirAugury.Common.WorkGroups;

/// <summary>
/// Jaro-Winkler similarity primitive used by <see cref="WorkGroupResolver"/>
/// to score fuzzy display-name matches.
/// </summary>
/// <remarks>
/// Standard Winkler algorithm:
/// <list type="bullet">
///   <item>Matching window: <c>max(|a|, |b|) / 2 - 1</c> (floor at 0).</item>
///   <item>Transpositions counted on the matched-pair sequence.</item>
///   <item>Prefix scale <c>p = 0.1</c>, capped at <c>4</c> prefix chars.</item>
///   <item>Inputs are case-folded via <see cref="char.ToUpperInvariant"/>.
///         No Unicode normalisation in this iteration.</item>
/// </list>
/// </remarks>
public static class JaroWinkler
{
    /// <summary>
    /// Returns the Jaro-Winkler similarity of two strings, in <c>[0.0, 1.0]</c>.
    /// </summary>
    public static double Compute(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;

        int matchWindow = Math.Max(0, (Math.Max(a.Length, b.Length) / 2) - 1);

        Span<bool> aMatched = a.Length <= 256 ? stackalloc bool[a.Length] : new bool[a.Length];
        Span<bool> bMatched = b.Length <= 256 ? stackalloc bool[b.Length] : new bool[b.Length];

        int matches = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int start = Math.Max(0, i - matchWindow);
            int end = Math.Min(b.Length - 1, i + matchWindow);
            char ac = char.ToUpperInvariant(a[i]);
            for (int j = start; j <= end; j++)
            {
                if (bMatched[j]) continue;
                if (char.ToUpperInvariant(b[j]) != ac) continue;
                aMatched[i] = true;
                bMatched[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0) return 0.0;

        int transpositions = 0;
        int k = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (!aMatched[i]) continue;
            while (k < b.Length && !bMatched[k]) k++;
            if (k >= b.Length) break;
            if (char.ToUpperInvariant(a[i]) != char.ToUpperInvariant(b[k])) transpositions++;
            k++;
        }
        transpositions /= 2;

        double m = matches;
        double jaro = ((m / a.Length) + (m / b.Length) + ((m - transpositions) / m)) / 3.0;

        int prefix = 0;
        int prefixCap = Math.Min(4, Math.Min(a.Length, b.Length));
        for (int i = 0; i < prefixCap; i++)
        {
            if (char.ToUpperInvariant(a[i]) != char.ToUpperInvariant(b[i])) break;
            prefix++;
        }

        const double scale = 0.1;
        return jaro + (prefix * scale * (1.0 - jaro));
    }
}
