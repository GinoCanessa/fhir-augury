using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace FhirAugury.Common.Api;

/// <summary>
/// A normalized Jira issue key and its canonical HL7 Jira browse URL.
/// </summary>
public sealed partial record JiraIssueKey
{
    private JiraIssueKey(string value)
        => Value = value;

    /// <summary>The canonical, upper-case issue key.</summary>
    public string Value { get; }

    /// <summary>The canonical HL7 Jira browse URL for this issue.</summary>
    public string BrowseUrl =>
        $"https://jira.hl7.org/browse/{Uri.EscapeDataString(Value)}";

    /// <summary>
    /// Parses the case-insensitive Jira grammar
    /// <c>[A-Z][A-Z0-9]*-[0-9]+</c>.
    /// </summary>
    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out JiraIssueKey? issueKey)
    {
        issueKey = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidate = value.Trim();
        if (!IssueKeyPattern().IsMatch(candidate))
        {
            return false;
        }

        issueKey = new JiraIssueKey(candidate.ToUpperInvariant());
        return true;
    }

    public override string ToString() => Value;

    [GeneratedRegex(
        @"^[A-Za-z][A-Za-z0-9]*-[0-9]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex IssueKeyPattern();
}
