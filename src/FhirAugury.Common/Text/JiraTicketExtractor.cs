using System.Text.RegularExpressions;

namespace FhirAugury.Common.Text;

/// <summary>A Jira ticket reference extracted from text.</summary>
/// <param name="JiraKey">Normalized Jira key (always FHIR-N form).</param>
/// <param name="OriginalLiteral">The literal as it appeared in the source text.</param>
/// <param name="Context">Surrounding text for context.</param>
public record JiraTicketMatch(string JiraKey, string OriginalLiteral, string Context);

/// <summary>
/// Extracts Jira ticket references from arbitrary text using multiple patterns.
/// Source-agnostic — returns normalized keys that any source can map to its own records.
/// </summary>
public static partial class JiraTicketExtractor
{
    [GeneratedRegex(@"\b(FHIR-\d+)\b")]
    private static partial Regex FhirKeyPattern();

    [GeneratedRegex(@"\b(JF-(\d+))\b")]
    private static partial Regex JfKeyPattern();

    [GeneratedRegex(@"\b(GF-(\d+))\b")]
    private static partial Regex GfKeyPattern();

    [GeneratedRegex(@"\bJ#(\d+)\b")]
    private static partial Regex JHashPattern();

    [GeneratedRegex(@"\bGF#(\d+)\b")]
    private static partial Regex GfHashPattern();

    [GeneratedRegex(@"https?://jira\.hl7\.org/browse/(FHIR-\d+)")]
    private static partial Regex JiraUrlPattern();

    /// <summary>
    /// Extracts all Jira ticket references from the given text.
    /// Deduplicates by JiraKey. Returns context (~160 chars) around each match.
    /// </summary>
    public static List<JiraTicketMatch> ExtractTickets(string text)
        => ExtractTickets(text, validJiraNumbers: null);

    /// <summary>
    /// Extracts all Jira ticket references, filtering J# and GF# patterns
    /// against an optional allowlist of valid Jira issue numbers.
    /// </summary>
    public static List<JiraTicketMatch> ExtractTickets(string text, HashSet<int>? validJiraNumbers)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        List<JiraTicketMatch> results = [];
        HashSet<string> seen = [];

        // 1. Jira URLs first (to avoid double-matching with key patterns)
        foreach (Match match in JiraUrlPattern().Matches(text))
        {
            string jiraKey = match.Groups[1].Value;
            if (seen.Add(jiraKey))
                results.Add(new JiraTicketMatch(jiraKey, jiraKey, CrossRefPatterns.GetSurroundingText(text, match.Index, 160)));
        }

        // 2. FHIR-N (already canonical)
        AddKeyMatches(results, seen, FhirKeyPattern(), text,
            m => m.Groups[1].Value, m => m.Groups[1].Value);

        // 3. JF-N → normalized FHIR-N
        AddKeyMatches(results, seen, JfKeyPattern(), text,
            m => $"FHIR-{m.Groups[2].Value}", m => m.Groups[1].Value);

        // 4. GF-N → normalized FHIR-N
        AddKeyMatches(results, seen, GfKeyPattern(), text,
            m => $"FHIR-{m.Groups[2].Value}", m => m.Groups[1].Value);

        // 5. Shorthand: J#N → FHIR-N
        foreach (Match match in JHashPattern().Matches(text))
        {
            string num = match.Groups[1].Value;
            if (validJiraNumbers is not null && int.TryParse(num, out int n) && !validJiraNumbers.Contains(n))
                continue;

            string jiraKey = $"FHIR-{num}";
            if (seen.Add(jiraKey))
                results.Add(new JiraTicketMatch(jiraKey, match.Value, CrossRefPatterns.GetSurroundingText(text, match.Index, 160)));
        }

        // 6. Shorthand: GF#N → FHIR-N
        foreach (Match match in GfHashPattern().Matches(text))
        {
            string num = match.Groups[1].Value;
            if (validJiraNumbers is not null && int.TryParse(num, out int n) && !validJiraNumbers.Contains(n))
                continue;

            string jiraKey = $"FHIR-{num}";
            if (seen.Add(jiraKey))
                results.Add(new JiraTicketMatch(jiraKey, match.Value, CrossRefPatterns.GetSurroundingText(text, match.Index, 160)));
        }

        return results;
    }

    private static void AddKeyMatches(
        List<JiraTicketMatch> results,
        HashSet<string> seen,
        Regex pattern,
        string text,
        Func<Match, string> extractKey,
        Func<Match, string> extractOriginal)
    {
        foreach (Match match in pattern.Matches(text))
        {
            string jiraKey = extractKey(match);
            if (seen.Add(jiraKey))
                results.Add(new JiraTicketMatch(jiraKey, extractOriginal(match), CrossRefPatterns.GetSurroundingText(text, match.Index, 160)));
        }
    }
}
