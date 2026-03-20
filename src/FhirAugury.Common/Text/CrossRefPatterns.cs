using System.Text.RegularExpressions;

namespace FhirAugury.Common.Text;

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
    public static List<(string TargetType, string TargetId, string Context)> ExtractLinks(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var results = new List<(string TargetType, string TargetId, string Context)>();
        var seen = new HashSet<(string, string)>();

        // Jira URLs (check before Jira keys to avoid double-matching)
        foreach (Match match in JiraUrlRegex().Matches(text))
        {
            var key = (TargetType: "jira", TargetId: match.Groups[1].Value);
            if (seen.Add(key))
                results.Add((key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
        }

        // Jira keys (skip if already found via URL)
        foreach (Match match in JiraKeyRegex().Matches(text))
        {
            var key = (TargetType: "jira", TargetId: match.Groups[1].Value);
            if (seen.Add(key))
                results.Add((key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
        }

        // Zulip URLs
        foreach (Match match in ZulipUrlRegex().Matches(text))
        {
            var streamId = match.Groups[1].Value;
            var topic = Uri.UnescapeDataString(match.Groups[2].Value.TrimEnd());
            var key = (TargetType: "zulip", TargetId: $"{streamId}:{topic}");
            if (seen.Add(key))
                results.Add((key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
        }

        // GitHub issue/PR URLs
        foreach (Match match in GitHubIssueUrlRegex().Matches(text))
        {
            var repo = match.Groups[1].Value;
            var number = match.Groups[2].Value;
            var key = (TargetType: "github", TargetId: $"{repo}#{number}");
            if (seen.Add(key))
                results.Add((key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
        }

        // GitHub short references (HL7/repo#123)
        foreach (Match match in GitHubShortRefRegex().Matches(text))
        {
            var key = (TargetType: "github", TargetId: match.Groups[1].Value);
            if (seen.Add(key))
                results.Add((key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
        }

        // Confluence URLs
        foreach (Match match in ConfluenceUrlRegex().Matches(text))
        {
            var pageId = match.Groups[1].Value;
            var key = (TargetType: "confluence", TargetId: pageId);
            if (seen.Add(key))
                results.Add((key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
        }

        return results;
    }

    /// <summary>
    /// Extracts ~100 characters of surrounding text around a match position.
    /// </summary>
    public static string GetSurroundingText(string fullText, int matchIndex, int contextChars = 100)
    {
        var halfContext = contextChars / 2;
        var start = Math.Max(0, matchIndex - halfContext);
        var end = Math.Min(fullText.Length, matchIndex + halfContext);
        var context = fullText[start..end].ReplaceLineEndings(" ").Trim();
        if (start > 0) context = "..." + context;
        if (end < fullText.Length) context += "...";
        return context;
    }
}
