using System.Text;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Computes a sanitized PascalCase rendering of an HL7 work group display
/// name suitable for use in URLs, file paths, and report directory names.
/// </summary>
/// <remarks>
/// Algorithm:
/// <list type="number">
///   <item>Replace <c>&amp;</c> with the literal token <c>And</c>.</item>
///   <item>Replace any non ASCII alphanumeric character with a single space.</item>
///   <item>Split on whitespace, dropping empty entries.</item>
///   <item>Capitalize the first character of each token; leave the rest
///         untouched so acronyms like <c>FHIR</c> survive.</item>
///   <item>Concatenate with no separator.</item>
/// </list>
/// </remarks>
public static class Hl7WorkGroupNameCleaner
{
    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        string ampReplaced = name.Replace("&", " And ");
        StringBuilder sanitized = new StringBuilder(ampReplaced.Length);
        foreach (char c in ampReplaced)
            sanitized.Append(char.IsAsciiLetterOrDigit(c) ? c : ' ');

        string[] tokens = sanitized.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        StringBuilder result = new StringBuilder(sanitized.Length);
        foreach (string t in tokens)
            result.Append(char.ToUpperInvariant(t[0])).Append(t.AsSpan(1));
        return result.ToString();
    }
}
