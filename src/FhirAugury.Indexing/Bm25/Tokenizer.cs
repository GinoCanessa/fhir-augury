using System.Text.RegularExpressions;

namespace FhirAugury.Indexing.Bm25;

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

    // Code blocks to strip (markdown-style)
    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.Singleline)]
    private static partial Regex CodeBlockRegex();

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

        var tokens = new List<string>();

        // Extract FHIR operations first (before stripping)
        foreach (Match match in FhirOperationRegex().Matches(text))
        {
            tokens.Add(match.Value.ToLowerInvariant());
        }

        // Extract FHIR paths (e.g., Patient.name.given)
        foreach (Match match in FhirPathRegex().Matches(text))
        {
            var path = match.Value;
            tokens.Add(path.ToLowerInvariant());

            // Also add individual components
            var components = path.Split('.');
            foreach (var component in components)
            {
                tokens.Add(component.ToLowerInvariant());
            }
        }

        // Strip noise: URLs, emails, code blocks
        var cleaned = CodeBlockRegex().Replace(text, " ");
        cleaned = UrlRegex().Replace(cleaned, " ");
        cleaned = EmailRegex().Replace(cleaned, " ");

        // Extract regular words
        foreach (Match match in WordRegex().Matches(cleaned))
        {
            tokens.Add(match.Value.ToLowerInvariant());
        }

        return tokens;
    }
}
