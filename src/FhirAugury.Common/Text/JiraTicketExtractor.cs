using System.Text.RegularExpressions;

namespace FhirAugury.Common.Text;

/// <summary>A Jira ticket reference extracted from text.</summary>
/// <param name="JiraKey">Normalized Jira key (e.g. FHIR-N, BALLOT-N, PSS-N, UP-N).</param>
/// <param name="OriginalLiteral">The literal as it appeared in the source text.</param>
/// <param name="Context">Surrounding text for context.</param>
public record JiraTicketMatch(string JiraKey, string OriginalLiteral, string Context);

/// <summary>
/// Extracts Jira ticket references from arbitrary text using multiple patterns.
/// Source-agnostic — returns normalized keys that any source can map to its own records.
/// </summary>
public static partial class JiraTicketExtractor
{
    /// <summary>
    /// Unified key/hash pattern. Named groups identify the canonical project.
    /// Matches: FHIR-N, JF-N, GF-N, J-N (→ FHIR-N) | BALLOT-N | PSS-N | UP-N
    /// Also matches hash variants: FHIR#N, JF#N, GF#N, J#N → FHIR-N, etc.
    /// </summary>
    [GeneratedRegex(@"(?<!/)\b(?:(?<fhir>FHIR|JF|GF|J)|(?<ballot>BALLOT)|(?<pss>PSS)|(?<up>UP))[-#](?<num>\d+)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnifiedKeyHashPattern();

    /// <summary>
    /// Combined URL pattern covering /browse/ and /projects/.../issues/ formats
    /// for all supported Jira projects.
    /// </summary>
    [GeneratedRegex(@"https?://jira\.hl7\.org/(?:browse/|projects/(?:FHIR|BALLOT|PSS|UP)/issues/)((?:FHIR|BALLOT|PSS|UP)-\d+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnifiedUrlPattern();

    /// <summary>
    /// Extracts all Jira ticket references from the given text.
    /// Deduplicates by JiraKey. Returns context (~160 chars) around each match.
    /// </summary>
    public static List<JiraTicketMatch> ExtractTickets(string text)
        => ExtractTickets(text, validJiraNumbers: null);

    /// <summary>
    /// Extracts all Jira ticket references, filtering FHIR hash-alias patterns
    /// against an optional allowlist of valid Jira issue numbers.
    /// </summary>
    public static List<JiraTicketMatch> ExtractTickets(string text, HashSet<int>? validJiraNumbers)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        List<JiraTicketMatch> results = [];
        HashSet<string> seen = [];

        // Pass 1: URL matches (canonical PREFIX-N already in capture group)
        foreach (Match match in UnifiedUrlPattern().Matches(text))
        {
            string jiraKey = match.Groups[1].Value.ToUpperInvariant();
            if (seen.Add(jiraKey))
                results.Add(new JiraTicketMatch(jiraKey, jiraKey, CrossRefPatterns.GetSurroundingText(text, match.Index, 160)));
        }

        // Pass 2: Key/hash matches
        foreach (Match match in UnifiedKeyHashPattern().Matches(text))
        {
            string? canonicalPrefix = GetCanonicalPrefix(match);
            if (canonicalPrefix is null) continue;

            string number = match.Groups["num"].Value;
            bool isHash = match.Value.Contains('#');

            // Validation filter: only FHIR hash aliases are filtered
            if (canonicalPrefix == "FHIR" && isHash
                && validJiraNumbers is not null
                && int.TryParse(number, out int n)
                && !validJiraNumbers.Contains(n))
                continue;

            string jiraKey = $"{canonicalPrefix}-{number}";
            if (seen.Add(jiraKey))
                results.Add(new JiraTicketMatch(jiraKey, match.Value, CrossRefPatterns.GetSurroundingText(text, match.Index, 160)));
        }

        return results;
    }

    private static string? GetCanonicalPrefix(Match match)
    {
        if (match.Groups["fhir"].Success) return "FHIR";
        if (match.Groups["ballot"].Success) return "BALLOT";
        if (match.Groups["pss"].Success) return "PSS";
        if (match.Groups["up"].Success) return "UP";
        return null;
    }
}
