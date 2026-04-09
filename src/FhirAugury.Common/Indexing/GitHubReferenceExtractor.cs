using System.Text.RegularExpressions;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Text;

namespace FhirAugury.Common.Indexing;

/// <summary>
/// Extracts GitHub issue/PR references from text (URLs and HL7/repo#NNN short refs).
/// Pure detection — text in, typed records out. No storage or DI dependencies.
/// </summary>
public static partial class GitHubReferenceExtractor
{
    [GeneratedRegex(@"https?://github\.com/(HL7/[^/]+)/(?:issues|pull)/(\d+)")]
    private static partial Regex GitHubIssueUrlRegex();

    [GeneratedRegex(@"\b(HL7/([a-zA-Z0-9_.-]+)#(\d+))\b")]
    private static partial Regex GitHubShortRefRegex();

    public static List<GitHubXRefRecord> GetReferences(
        string sourceType, string sourceId, params string[] texts)
    {
        List<GitHubXRefRecord> results = [];
        HashSet<string> seen = [];

        foreach (string text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            // GitHub issue/PR URLs
            foreach (Match match in GitHubIssueUrlRegex().Matches(text))
            {
                string repoFullName = match.Groups[1].Value;
                int issueNumber = int.Parse(match.Groups[2].Value);
                string key = $"{repoFullName}#{issueNumber}";
                if (!seen.Add(key)) continue;

                results.Add(new GitHubXRefRecord
                {
                    Id = GitHubXRefRecord.GetIndex(),
                    ContentType = sourceType,
                    SourceId = sourceId,
                    LinkType = "mentions",
                    Context = CrossRefPatterns.GetSurroundingText(text, match.Index, 160),
                    RepoFullName = repoFullName,
                    IssueNumber = issueNumber,
                });
            }

            // GitHub short references (HL7/repo#123)
            foreach (Match match in GitHubShortRefRegex().Matches(text))
            {
                string repoFullName = $"HL7/{match.Groups[2].Value}";
                int issueNumber = int.Parse(match.Groups[3].Value);
                string key = $"{repoFullName}#{issueNumber}";
                if (!seen.Add(key)) continue;

                results.Add(new GitHubXRefRecord
                {
                    Id = GitHubXRefRecord.GetIndex(),
                    ContentType = sourceType,
                    SourceId = sourceId,
                    LinkType = "mentions",
                    Context = CrossRefPatterns.GetSurroundingText(text, match.Index, 160),
                    RepoFullName = repoFullName,
                    IssueNumber = issueNumber,
                });
            }
        }
        return results;
    }
}
