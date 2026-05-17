using System.Text.RegularExpressions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing.Hydration;

/// <summary>
/// Inline parser for the GitHub item-id shapes the preparer-hydrator handles.
/// Mirrors the spirit of <c>GitHubUrlHelper.ResolveXRef</c>/<c>TryParseFileId</c>
/// in <c>FhirAugury.Source.GitHub</c>; duplicated here to avoid a project
/// reference into source-service internals (see plan §Phase 4 Step 2).
/// </summary>
internal static partial class GitHubItemKey
{
    [GeneratedRegex(@"^(?<owner>[^/]+)/(?<repo>[^#]+)#(?<number>\d+)$", RegexOptions.Compiled)]
    private static partial Regex IssueRegex();

    [GeneratedRegex(@"^(?<owner>[^/]+)/(?<repo>[^:]+):(?<path>.+)$", RegexOptions.Compiled)]
    private static partial Regex FilePathRegex();

    public static bool TryParse(string id, out ParsedGitHubItemKey parsed)
    {
        if (!string.IsNullOrEmpty(id))
        {
            Match issue = IssueRegex().Match(id);
            if (issue.Success && int.TryParse(issue.Groups["number"].Value, out int number))
            {
                parsed = new ParsedGitHubItemKey(
                    Owner: issue.Groups["owner"].Value,
                    Repo: issue.Groups["repo"].Value,
                    Number: number,
                    Path: null);
                return true;
            }

            Match file = FilePathRegex().Match(id);
            if (file.Success)
            {
                parsed = new ParsedGitHubItemKey(
                    Owner: file.Groups["owner"].Value,
                    Repo: file.Groups["repo"].Value,
                    Number: null,
                    Path: file.Groups["path"].Value);
                return true;
            }
        }

        parsed = default;
        return false;
    }
}

internal readonly record struct ParsedGitHubItemKey(string Owner, string Repo, int? Number, string? Path);
