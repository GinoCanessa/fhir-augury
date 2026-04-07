using System.Text.RegularExpressions;

namespace FhirAugury.Common.Api;

/// <summary>
/// Determines the likely source type of a value string based on known patterns.
/// Used by sources to decide which xref table(s) to search when sourceType is not explicitly provided.
/// </summary>
public static partial class ValueFormatDetector
{
    /// <summary>
    /// Detects the source type from a value string. Returns null if ambiguous.
    /// </summary>
    public static string? DetectSourceType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Jira key: FHIR-50783, GF-1234 (uppercase project + dash + digits)
        if (JiraKeyRegex().IsMatch(value))
            return SourceSystems.Jira;

        // GitHub issue: owner/repo#42
        if (GitHubIssueRegex().IsMatch(value))
            return SourceSystems.GitHub;

        // GitHub file: owner/repo:path/to/file
        if (GitHubFileRegex().IsMatch(value))
            return SourceSystems.GitHub;

        // FHIR element: Resource.element.path (dotted path starting with uppercase)
        if (FhirElementRegex().IsMatch(value))
            return SourceSystems.Fhir;

        // Zulip: streamId:topicName (number:text)
        if (ZulipStreamTopicRegex().IsMatch(value))
            return SourceSystems.Zulip;

        // Zulip message: msg:12345
        if (value.StartsWith("msg:", StringComparison.OrdinalIgnoreCase))
            return SourceSystems.Zulip;

        // Confluence: page:12345
        if (value.StartsWith("page:", StringComparison.OrdinalIgnoreCase))
            return SourceSystems.Confluence;

        // Pure numeric could be Confluence pageId or Zulip messageId — ambiguous
        return null;
    }

    /// <summary>
    /// Returns true if the value matches a Jira key pattern.
    /// </summary>
    public static bool IsJiraKey(string value) => JiraKeyRegex().IsMatch(value);

    /// <summary>
    /// Returns true if the value matches a GitHub issue pattern (owner/repo#N).
    /// </summary>
    public static bool IsGitHubIssue(string value) => GitHubIssueRegex().IsMatch(value);

    /// <summary>
    /// Returns true if the value matches a GitHub file pattern (owner/repo:path).
    /// </summary>
    public static bool IsGitHubFile(string value) => GitHubFileRegex().IsMatch(value);

    /// <summary>
    /// Returns true if the value matches a FHIR element path pattern.
    /// </summary>
    public static bool IsFhirElement(string value) => FhirElementRegex().IsMatch(value);

    /// <summary>
    /// Tries to parse a GitHub issue reference into its components.
    /// </summary>
    public static bool TryParseGitHubIssue(string value, out string repoFullName, out int issueNumber)
    {
        Match match = GitHubIssueRegex().Match(value);
        if (match.Success)
        {
            repoFullName = match.Groups[1].Value;
            issueNumber = int.Parse(match.Groups[2].Value);
            return true;
        }
        repoFullName = "";
        issueNumber = 0;
        return false;
    }

    [GeneratedRegex(@"^[A-Z][A-Z0-9]+-\d+$")]
    private static partial Regex JiraKeyRegex();

    [GeneratedRegex(@"^([a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+)#(\d+)$")]
    private static partial Regex GitHubIssueRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+:.+$")]
    private static partial Regex GitHubFileRegex();

    [GeneratedRegex(@"^[A-Z][a-zA-Z]+(\.[a-zA-Z][a-zA-Z0-9]*)+$")]
    private static partial Regex FhirElementRegex();

    [GeneratedRegex(@"^\d+:.+$")]
    private static partial Regex ZulipStreamTopicRegex();
}
