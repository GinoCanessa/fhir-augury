namespace FhirAugury.Server.Terminology;

/// <summary>
/// Pure-string normalization helpers used across the ingestion and
/// matching layers. Kept side-effect-free and dependency-free so they
/// are trivially unit-testable and shareable between the writer and
/// the matchers.
/// </summary>
public static class TerminologyTextNormalizer
{
    /// <summary>
    /// Canonicalizes a FHIR canonical URL for equality / lookup:
    /// lowercased, trimmed, with any trailing <c>/</c> removed and a
    /// trailing <c>|&lt;version&gt;</c> stripped.
    /// </summary>
    public static string NormalizeCanonicalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        string s = url.Trim();
        int pipe = s.IndexOf('|');
        if (pipe >= 0) s = s[..pipe];

        s = s.TrimEnd('/');
        return s.ToLowerInvariant();
    }

    /// <summary>
    /// Lowercases, collapses whitespace, and strips ASCII punctuation
    /// from a display string. Returns <c>null</c> when input is null/empty.
    /// </summary>
    public static string? NormalizeDisplay(string? display)
    {
        if (string.IsNullOrWhiteSpace(display)) return null;

        System.Text.StringBuilder sb = new(display.Length);
        bool lastSpace = false;

        foreach (char raw in display.Trim())
        {
            char c = char.ToLowerInvariant(raw);
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastSpace = false;
            }
            else if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c))
            {
                if (!lastSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                lastSpace = false;
            }
        }

        // Trim trailing single space introduced above.
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length -= 1;
        return sb.Length == 0 ? null : sb.ToString();
    }
}
