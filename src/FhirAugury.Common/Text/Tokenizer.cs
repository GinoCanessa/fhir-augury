using System.Text.RegularExpressions;

namespace FhirAugury.Common.Text;

/// <summary>
/// Tokenizes text for BM25 indexing, preserving FHIR-specific tokens.
/// </summary>
public static partial class Tokenizer
{
    // FHIR element paths: Resource.element.subElement
    [GeneratedRegex(@"[A-Z][a-zA-Z]+(?:\.[a-zA-Z][a-zA-Z0-9]*)+")]
    private static partial Regex FhirPathRegex();

    // FHIR operations: $validate, $expand, etc.
    [GeneratedRegex(@"\$[a-zA-Z][a-zA-Z0-9-]*")]
    private static partial Regex FhirOperationRegex();

    // URLs to strip
    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlRegex();

    // Email addresses to strip
    [GeneratedRegex(@"\S+@\S+\.\S+")]
    private static partial Regex EmailRegex();

    // Word splitting: letters and digits
    [GeneratedRegex(@"[a-zA-Z0-9]+")]
    private static partial Regex WordRegex();

    /// <summary>
    /// Tokenizes text into a list of normalized tokens, preserving FHIR paths and operations.
    /// </summary>
    /// <param name="text">The text to tokenize.</param>
    /// <returns>A list of lowercase tokens.</returns>
    public static List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<string> tokens = new List<string>();

        // Extract FHIR operations first (before stripping)
        foreach (Match match in FhirOperationRegex().Matches(text))
        {
            tokens.Add(string.Create(match.Length, match, static (span, m) =>
                m.ValueSpan.ToLowerInvariant(span)));
        }

        // Extract FHIR paths (e.g., Patient.name.given)
        foreach (Match match in FhirPathRegex().Matches(text))
        {
            ReadOnlySpan<char> path = match.ValueSpan;
            tokens.Add(string.Create(path.Length, match, static (span, m) =>
                m.ValueSpan.ToLowerInvariant(span)));

            // Also add individual components using span-based splitting
            foreach (Range segment in path.Split('.'))
            {
                ReadOnlySpan<char> component = path[segment];
                tokens.Add(string.Create(component.Length, component.ToString(), static (span, s) =>
                    s.AsSpan().ToLowerInvariant(span)));
            }
        }

        // Strip noise: URLs, emails, code blocks
        string cleaned = TextPatterns.CodeBlockRegex().Replace(text, " ");
        cleaned = UrlRegex().Replace(cleaned, " ");
        cleaned = EmailRegex().Replace(cleaned, " ");

        // Extract regular words
        foreach (Match match in WordRegex().Matches(cleaned))
        {
            tokens.Add(string.Create(match.Length, match, static (span, m) =>
                m.ValueSpan.ToLowerInvariant(span)));
        }

        return tokens;
    }
}
