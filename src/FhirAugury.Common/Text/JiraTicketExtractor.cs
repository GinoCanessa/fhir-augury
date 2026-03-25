using System.Text.RegularExpressions;

namespace FhirAugury.Common.Text;

/// <summary>A Jira ticket reference extracted from text.</summary>
public record JiraTicketMatch(string JiraKey, string Context);

/// <summary>
/// Extracts Jira ticket references from arbitrary text using multiple patterns.
/// Source-agnostic — returns normalized keys that any source can map to its own records.
/// </summary>
public static partial class JiraTicketExtractor
{
    [GeneratedRegex(@"\b(FHIR-\d+)\b")]
    private static partial Regex FhirKeyPattern();

    [GeneratedRegex(@"\b(JF-\d+)\b")]
    private static partial Regex JfKeyPattern();

    [GeneratedRegex(@"\b(GF-\d+)\b")]
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
                results.Add(new JiraTicketMatch(jiraKey, CrossRefPatterns.GetSurroundingText(text, match.Index, 160)));
        }

        // 2. Explicit key patterns: FHIR-N, JF-N, GF-N
        AddKeyMatches(results, seen, FhirKeyPattern(), text, m => m.Groups[1].Value);
        AddKeyMatches(results, seen, JfKeyPattern(), text, m => m.Groups[1].Value);
        AddKeyMatches(results, seen, GfKeyPattern(), text, m => m.Groups[1].Value);

        // 3. Shorthand: J#N → FHIR-N
        foreach (Match match in JHashPattern().Matches(text))
        {
            string num = match.Groups[1].Value;
            if (validJiraNumbers is not null && int.TryParse(num, out int n) && !validJiraNumbers.Contains(n))
                continue;

            string jiraKey = $"FHIR-{num}";
            if (seen.Add(jiraKey))
                results.Add(new JiraTicketMatch(jiraKey, CrossRefPatterns.GetSurroundingText(text, match.Index, 160)));
        }

        // 4. Shorthand: GF#N → GF-N
        foreach (Match match in GfHashPattern().Matches(text))
        {
            string num = match.Groups[1].Value;
            if (validJiraNumbers is not null && int.TryParse(num, out int n) && !validJiraNumbers.Contains(n))
                continue;

            string jiraKey = $"GF-{num}";
            if (seen.Add(jiraKey))
                results.Add(new JiraTicketMatch(jiraKey, CrossRefPatterns.GetSurroundingText(text, match.Index, 160)));
        }

        return results;
    }

    private static void AddKeyMatches(
        List<JiraTicketMatch> results,
        HashSet<string> seen,
        Regex pattern,
        string text,
        Func<Match, string> extractKey)
    {
        foreach (Match match in pattern.Matches(text))
        {
            string jiraKey = extractKey(match);
            if (seen.Add(jiraKey))
                results.Add(new JiraTicketMatch(jiraKey, CrossRefPatterns.GetSurroundingText(text, match.Index, 160)));
        }
    }
}
