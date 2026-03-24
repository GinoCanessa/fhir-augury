using System.Text;
using System.Text.RegularExpressions;

namespace FhirAugury.Common.Text;

/// <summary>A cross-reference link extracted from text.</summary>
public record CrossReference(string TargetType, string TargetId, string Context);

/// <summary>
/// Regex-based cross-reference extraction patterns.
/// Extracts identifiers for Jira, Zulip, Confluence, and GitHub from text.
/// </summary>
public static partial class CrossRefPatterns
{
    [GeneratedRegex(@"\b(FHIR-\d+)\b")]
    private static partial Regex JiraKeyRegex();

    [GeneratedRegex(@"https?://jira\.hl7\.org/browse/(FHIR-\d+)")]
    private static partial Regex JiraUrlRegex();

    [GeneratedRegex(@"https?://chat\.fhir\.org/#narrow/stream/(\d+)[^/]*/topic/([^\s?#]+)")]
    private static partial Regex ZulipUrlRegex();

    [GeneratedRegex(@"https?://github\.com/(HL7/[^/]+)/(?:issues|pull)/(\d+)")]
    private static partial Regex GitHubIssueUrlRegex();

    [GeneratedRegex(@"\b(HL7/[a-zA-Z0-9_-]+#\d+)\b")]
    private static partial Regex GitHubShortRefRegex();

    [GeneratedRegex(@"https?://confluence\.hl7\.org/.*?/(\d+)")]
    private static partial Regex ConfluenceUrlRegex();

    /// <summary>
    /// Extracts cross-reference links from a text string.
    /// </summary>
    public static List<CrossReference> ExtractLinks(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        List<CrossReference> results = new List<CrossReference>();
        HashSet<(string, string)> seen = new HashSet<(string, string)>();

        // Jira URLs (check before Jira keys to avoid double-matching)
        foreach (Match match in JiraUrlRegex().Matches(text))
        {
            (string TargetType, string TargetId) key = (TargetType: "jira", TargetId: match.Groups[1].Value);
            if (seen.Add(key))
                results.Add(new CrossReference(key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
        }

        // Jira keys (skip if already found via URL)
        foreach (Match match in JiraKeyRegex().Matches(text))
        {
            (string TargetType, string TargetId) key = (TargetType: "jira", TargetId: match.Groups[1].Value);
            if (seen.Add(key))
                results.Add(new CrossReference(key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
        }

        // Zulip URLs
        foreach (Match match in ZulipUrlRegex().Matches(text))
        {
            string streamId = match.Groups[1].Value;
            string topic = Uri.UnescapeDataString(match.Groups[2].Value.TrimEnd());
            (string TargetType, string TargetId) key = (TargetType: "zulip", TargetId: $"{streamId}:{topic}");
            if (seen.Add(key))
                results.Add(new CrossReference(key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
        }

        // GitHub issue/PR URLs
        foreach (Match match in GitHubIssueUrlRegex().Matches(text))
        {
            string repo = match.Groups[1].Value;
            string number = match.Groups[2].Value;
            (string TargetType, string TargetId) key = (TargetType: "github", TargetId: $"{repo}#{number}");
            if (seen.Add(key))
                results.Add(new CrossReference(key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
        }

        // GitHub short references (HL7/repo#123)
        foreach (Match match in GitHubShortRefRegex().Matches(text))
        {
            (string TargetType, string TargetId) key = (TargetType: "github", TargetId: match.Groups[1].Value);
            if (seen.Add(key))
                results.Add(new CrossReference(key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
        }

        // Confluence URLs
        foreach (Match match in ConfluenceUrlRegex().Matches(text))
        {
            string pageId = match.Groups[1].Value;
            (string TargetType, string TargetId) key = (TargetType: "confluence", TargetId: pageId);
            if (seen.Add(key))
                results.Add(new CrossReference(key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
        }

        return results;
    }

    /// <summary>
    /// Extracts ~100 characters of surrounding text around a match position.
    /// </summary>
    public static string GetSurroundingText(string fullText, int matchIndex, int contextChars = 100)
    {
        int halfContext = contextChars / 2;
        int start = Math.Max(0, matchIndex - halfContext);
        int end = Math.Min(fullText.Length, matchIndex + halfContext);

        StringBuilder sb = new StringBuilder(contextChars + 6);
        if (start > 0)
            sb.Append("...");
        sb.Append(fullText.AsSpan(start, end - start));
        sb.Replace('\r', ' ').Replace('\n', ' ');
        if (end < fullText.Length)
            sb.Append("...");

        return sb.ToString().Trim();
    }
}
