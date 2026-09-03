using System.Net;
using System.Text.RegularExpressions;
using FhirAugury.Tools.TicketMdToDb.Compilation;

namespace FhirAugury.Tools.TicketMdToDb.Mapping;

public static partial class RelatedReferenceMapper
{
    public static void Map(
        ImportedTicketReview review,
        TicketReviewDocument document,
        TicketReviewSection? linkedJira,
        TicketReviewSection? relatedJira,
        TicketReviewSection? relatedZulip,
        TicketReviewSection? relatedGitHub,
        TicketReviewSection? repoContext)
    {
        MapJira(review, document, linkedJira, "linked");
        MapJira(review, document, relatedJira, "related");
        MapZulip(review, document, relatedZulip);
        MapGitHub(review, document, relatedGitHub);
        MapRepositories(review, document, repoContext);
    }

    private static void MapJira(
        ImportedTicketReview review,
        TicketReviewDocument document,
        TicketReviewSection? section,
        string linkType)
    {
        foreach (MappedLink link in LinksInSection(document, section, $"jira-{linkType}-reference"))
        {
            Match match = JiraBrowseRegex().Match(link.Url);
            if (!match.Success)
            {
                if (link.Url.Contains("jira", StringComparison.OrdinalIgnoreCase))
                {
                    review.Diagnostics.Add(ImportDiagnostic.Blocking(
                        "malformed-jira-reference",
                        $"Could not normalize Jira reference {link.Url}.",
                        document.Source.RelativePath,
                        document.Source.Key));
                }
                continue;
            }

            review.RelatedJira.Add(new ImportedJiraCandidate(
                match.Groups["key"].Value.ToUpperInvariant(),
                linkType,
                link.Label,
                [link.Evidence]));
        }
    }

    private static void MapZulip(
        ImportedTicketReview review,
        TicketReviewDocument document,
        TicketReviewSection? section)
    {
        foreach (MappedLink link in LinksInSection(document, section, "zulip-reference"))
        {
            if (!TryNormalizeZulip(link.Label, link.Url, out string? threadId))
            {
                if (link.Url.Contains("chat.fhir.org", StringComparison.OrdinalIgnoreCase))
                {
                    review.Diagnostics.Add(ImportDiagnostic.Blocking(
                        "malformed-zulip-reference",
                        $"Could not normalize Zulip reference {link.Url}.",
                        document.Source.RelativePath,
                        document.Source.Key));
                }
                continue;
            }

            review.RelatedZulip.Add(new ImportedZulipCandidate(
                threadId!,
                link.Label,
                [link.Evidence]));
        }
    }

    private static void MapGitHub(
        ImportedTicketReview review,
        TicketReviewDocument document,
        TicketReviewSection? section)
    {
        foreach (MappedLink link in LinksInSection(document, section, "github-reference"))
        {
            if (!TryNormalizeGitHub(link.Url, out string? itemId, out string? repo, out bool unsupportedCommit))
            {
                if (link.Url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                {
                    review.Diagnostics.Add(ImportDiagnostic.Blocking(
                        "malformed-github-reference",
                        $"Could not normalize GitHub reference {link.Url}.",
                        document.Source.RelativePath,
                        document.Source.Key));
                }
                continue;
            }

            review.RelatedGitHub.Add(new ImportedGitHubCandidate(
                itemId!,
                link.Label,
                unsupportedCommit,
                [link.Evidence]));
            if (repo is not null)
            {
                review.Repositories.Add(new ImportedRepoCandidate(
                    repo,
                    string.Empty,
                    $"Referenced by {link.Label}",
                    [link.Evidence]));
            }
            if (unsupportedCommit)
            {
                review.Diagnostics.Add(ImportDiagnostic.NonBlocking(
                    "unsupported-github-commit",
                    $"GitHub commit is preserved for unresolved hydration: {itemId}.",
                    document.Source.RelativePath,
                    document.Source.Key));
            }
        }
    }

    private static void MapRepositories(
        ImportedTicketReview review,
        TicketReviewDocument document,
        TicketReviewSection? section)
    {
        if (section is null)
        {
            return;
        }

        foreach (TicketReviewHeading heading in document.Headings.Where(
                     heading => heading.StartOffset >= section.BodyStartOffset
                         && heading.StartOffset <= section.EndOffset))
        {
            Match match = RepoHeadingRegex().Match(heading.Text);
            if (!match.Success)
            {
                continue;
            }

            SourceEvidence evidence = document.Evidence(
                heading.StartOffset,
                heading.EndOffset,
                heading.HeadingPath,
                "repo-context-heading");
            review.Repositories.Add(new ImportedRepoCandidate(
                match.Groups["repo"].Value,
                match.Groups["category"].Value,
                document.Body(section),
                [evidence]));
        }
    }

    private static IReadOnlyList<MappedLink> LinksInSection(
        TicketReviewDocument document,
        TicketReviewSection? section,
        string ruleId)
    {
        if (section is null)
        {
            return [];
        }

        List<MappedLink> links = document.Links
            .Where(link => section.Contains(link.StartOffset))
            .Select(link => new MappedLink(
                link.Label,
                link.Url,
                document.Evidence(
                    link.StartOffset,
                    link.EndOffset,
                    link.HeadingPath,
                    ruleId)))
            .ToList();

        string body = document.Slice(section.BodyStartOffset, section.EndOffset);
        foreach (Match match in ReversedLinkRegex().Matches(body))
        {
            int start = section.BodyStartOffset + match.Index;
            int end = start + match.Length - 1;
            if (links.Any(link => link.Evidence.StartOffset == start && link.Evidence.EndOffset == end))
            {
                continue;
            }

            links.Add(new MappedLink(
                match.Groups["label"].Value.Trim(),
                match.Groups["url"].Value.Trim(),
                document.Evidence(start, end, section.Heading.HeadingPath, $"{ruleId}-reversed")));
        }

        return links
            .OrderBy(link => link.Evidence.StartOffset)
            .ThenBy(link => link.Url, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryNormalizeZulip(string label, string url, out string? threadId)
    {
        string cleaned = WebUtility.HtmlDecode(label)
            .Trim()
            .Trim('(', ')', '[', ']');
        string[] labelParts = cleaned.Split('>', 2, StringSplitOptions.TrimEntries);
        if (labelParts.Length == 2
            && !string.IsNullOrWhiteSpace(labelParts[0])
            && !string.IsNullOrWhiteSpace(labelParts[1]))
        {
            threadId = $"{labelParts[0]}:{labelParts[1]}";
            return true;
        }

        Match urlMatch = ZulipUrlRegex().Match(Uri.UnescapeDataString(url));
        if (urlMatch.Success)
        {
            string stream = DecodeZulipSegment(urlMatch.Groups["stream"].Value);
            string topic = DecodeZulipSegment(urlMatch.Groups["topic"].Value);
            threadId = $"{stream}:{topic}";
            return true;
        }

        threadId = null;
        return false;
    }

    private static string DecodeZulipSegment(string value) =>
        value.Replace(".24", "$", StringComparison.OrdinalIgnoreCase)
            .Replace('.', ' ')
            .Trim();

    private static bool TryNormalizeGitHub(
        string url,
        out string? itemId,
        out string? repo,
        out bool unsupportedCommit)
    {
        Match match = GitHubUrlRegex().Match(url);
        if (!match.Success)
        {
            itemId = null;
            repo = null;
            unsupportedCommit = false;
            return false;
        }

        string owner = match.Groups["owner"].Value;
        string repoName = match.Groups["repo"].Value;
        string kind = match.Groups["kind"].Value.ToLowerInvariant();
        string value = match.Groups["value"].Value;
        repo = $"{owner}/{repoName}";
        unsupportedCommit = kind is "commit" or "commits";
        if (unsupportedCommit)
        {
            itemId = $"https://github.com/{owner}/{repoName}/commit/{value}";
            return true;
        }

        if (kind is "issues" or "pull")
        {
            Match number = LeadingNumberRegex().Match(value);
            if (!number.Success)
            {
                itemId = null;
                return false;
            }

            itemId = $"{owner}/{repoName}#{number.Value}";
            return true;
        }

        if (kind is "blob" or "tree")
        {
            string[] parts = value.Split('/', 2);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[1]))
            {
                itemId = null;
                return false;
            }

            itemId = $"{owner}/{repoName}:{parts[1]}";
            return true;
        }

        itemId = null;
        return false;
    }

    private sealed record MappedLink(
        string Label,
        string Url,
        SourceEvidence Evidence);

    [GeneratedRegex(@"https?://jira\.hl7\.org/browse/(?<key>[A-Z][A-Z0-9]+-\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex JiraBrowseRegex();

    [GeneratedRegex(
        @"https?://github\.com/(?<owner>[^/\s]+)/(?<repo>[^/\s]+)/(?<kind>issues|pull|blob|tree|commit|commits)/(?<value>[^\s?#]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex GitHubUrlRegex();

    [GeneratedRegex(@"^\d+")]
    private static partial Regex LeadingNumberRegex();

    [GeneratedRegex(
        @"#narrow/(?:stream|channel)/(?<stream>[^/]+)/topic/(?<topic>[^/#]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ZulipUrlRegex();

    [GeneratedRegex(@"\(\[(?<label>[^\]]+)\]\)\[(?<url>https?://[^\]]+)\]")]
    private static partial Regex ReversedLinkRegex();

    [GeneratedRegex(@"^(?<repo>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)(?:\s+\((?<category>[^)]+)\))?$")]
    private static partial Regex RepoHeadingRegex();
}
