using System.Text.RegularExpressions;
using FhirAugury.Tools.TicketMdToDb.Mapping;

namespace FhirAugury.Tools.TicketMdToDb.Compilation;

public sealed partial class CanonicalTicketReviewDialect : ITicketReviewDialect
{
    private static readonly HashSet<string> LegacyKeys = new(StringComparer.Ordinal)
    {
        "FHIR-10333",
        "FHIR-10654",
        "FHIR-12563",
        "FHIR-13634",
        "FHIR-16662",
        "FHIR-17156",
    };

    private static readonly string[] RequiredHeadings =
    [
        "Summary",
        "Details",
        "Keywords",
        "Linked Jira Tickets",
        "Related Jira Tickets",
        "Related Zulip Discussions",
        "Related GitHub Items",
        "Repo Context",
        "Proposed Dispositions",
    ];

    public string Id => "canonical-v1";

    public bool Recognizes(TicketReviewDocument document) =>
        !LegacyKeys.Contains(document.Source.Key)
        && document.HasOrderedHeadings(RequiredHeadings);

    public ImportedTicketReview Parse(TicketReviewDocument document)
    {
        ImportedTicketReview review = new()
        {
            Key = document.Source.Key,
            DialectId = Id,
            Source = document.Source,
        };

        TicketReviewSection? summary = document.FindSectionUntil("Summary", "Details");
        TicketReviewSection? details = document.FindSectionUntil("Details", "Keywords");
        TicketReviewSection? linkedJira = document.FindSectionUntil("Linked Jira Tickets", "Related Jira Tickets");
        TicketReviewSection? relatedJira = document.FindSectionUntil("Related Jira Tickets", "Related Zulip Discussions");
        TicketReviewSection? relatedZulip = document.FindSectionUntil("Related Zulip Discussions", "Related GitHub Items");
        TicketReviewSection? relatedGitHub = document.FindSectionUntil("Related GitHub Items", "Repo Context");
        TicketReviewSection? repoContext = document.FindSectionUntil("Repo Context", "Proposed Dispositions");

        TicketReviewDialectHelpers.SetSectionScalar(
            review,
            document,
            PreparedTicketFieldNames.RequestSummary,
            summary,
            "canonical-summary");
        TicketReviewDialectHelpers.SetSectionScalar(
            review,
            document,
            PreparedTicketFieldNames.CommentSummary,
            details,
            "canonical-details");
        TicketReviewDialectHelpers.SetSectionScalar(
            review,
            document,
            PreparedTicketFieldNames.LinkedTicketSummary,
            linkedJira,
            "canonical-linked-jira-summary");
        TicketReviewDialectHelpers.SetSectionScalar(
            review,
            document,
            PreparedTicketFieldNames.RelatedTicketSummary,
            relatedJira,
            "canonical-related-jira-summary");
        TicketReviewDialectHelpers.SetSectionScalar(
            review,
            document,
            PreparedTicketFieldNames.RelatedZulipSummary,
            relatedZulip,
            "canonical-related-zulip-summary");
        TicketReviewDialectHelpers.SetSectionScalar(
            review,
            document,
            PreparedTicketFieldNames.RelatedGitHubSummary,
            relatedGitHub,
            "canonical-related-github-summary");

        string existing = ExtractBoldField(document.Body(details), "Resolution description");
        TicketReviewDialectHelpers.SetScalar(
            review,
            document,
            PreparedTicketFieldNames.ExistingProposed,
            existing,
            details,
            "canonical-existing-resolution");

        MapDisposition(review, document, "Disposition A", "A");
        MapDisposition(review, document, "Disposition B", "B");
        MapDisposition(review, document, "Disposition C", "C");
        MapRecommendation(review, document, document.FindSectionStartingWith("Recommendation"), "canonical-recommendation");

        RelatedReferenceMapper.Map(
            review,
            document,
            linkedJira,
            relatedJira,
            relatedZulip,
            relatedGitHub,
            repoContext);

        Dictionary<string, (SourceRangeDisposition, string)> classifications =
            RequiredHeadings.ToDictionary(
                TicketReviewDocument.NormalizeHeading,
                heading => heading == "Keywords"
                    ? (SourceRangeDisposition.IntentionallyUnmapped, "keywords-have-no-persistence-field")
                    : (SourceRangeDisposition.Mapped, $"canonical-{heading.ToLowerInvariant().Replace(' ', '-')}"),
                StringComparer.OrdinalIgnoreCase);
        review.SourceRanges.AddRange(document.ClassifySentinelSections(
            RequiredHeadings,
            classifications,
            "canonical-noncontract-section"));
        return review;
    }

    internal static void MapDisposition(
        ImportedTicketReview review,
        TicketReviewDocument document,
        string headingPrefix,
        string suffix)
    {
        TicketReviewSection? section = document.FindSectionStartingWith(headingPrefix);
        string body = document.Body(section);
        string proposal = ExtractHeadingOrBoldField(body, "Proposal", "Justification");
        string justification = ExtractHeadingOrBoldField(body, "Justification", null);
        TicketReviewDialectHelpers.SetScalar(
            review,
            document,
            $"Proposal{suffix}",
            proposal,
            section,
            $"canonical-disposition-{suffix.ToLowerInvariant()}-proposal");
        TicketReviewDialectHelpers.SetScalar(
            review,
            document,
            $"Proposal{suffix}Justification",
            justification,
            section,
            $"canonical-disposition-{suffix.ToLowerInvariant()}-justification");
    }

    internal static void MapRecommendation(
        ImportedTicketReview review,
        TicketReviewDocument document,
        TicketReviewSection? section,
        string rulePrefix,
        string? forcedRecommendation = null)
    {
        string body = document.Body(section);
        string? recommendation = forcedRecommendation ?? ParseRecommendation(body);
        string justification = ExtractRecommendationJustification(body);
        TicketReviewDialectHelpers.SetScalar(
            review,
            document,
            PreparedTicketFieldNames.Recommendation,
            recommendation,
            section,
            $"{rulePrefix}-category",
            recommendation is null ? ImportedValueState.Ambiguous : ImportedValueState.Missing);
        TicketReviewDialectHelpers.SetScalar(
            review,
            document,
            PreparedTicketFieldNames.RecommendationJustification,
            justification,
            section,
            $"{rulePrefix}-justification");
    }

    internal static string ExtractHeadingOrBoldField(
        string markdown,
        string label,
        string? nextLabel)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        string next = nextLabel is null
            ? @"\z"
            : $@"(?=^\s*(?:#{{1,6}}\s+|\*\*){Regex.Escape(nextLabel)}\b)";
        Match heading = Regex.Match(
            markdown,
            $@"(?ims)^\s*#{{1,6}}\s+{Regex.Escape(label)}\s*$\s*(?<value>.*?){next}");
        if (heading.Success)
        {
            return heading.Groups["value"].Value.Trim();
        }

        Match bold = Regex.Match(
            markdown,
            nextLabel is null
                ? $@"(?ims)^\s*\*\*{Regex.Escape(label)}:\*\*\s*(?<value>.*)\z"
                : $@"(?ims)^\s*\*\*{Regex.Escape(label)}:\*\*\s*(?<value>.*?)(?=^\s*\*\*{Regex.Escape(nextLabel)}:\*\*)");
        return bold.Success ? bold.Groups["value"].Value.Trim() : string.Empty;
    }

    internal static string ExtractBoldField(string markdown, string label)
    {
        Match match = Regex.Match(
            markdown,
            $@"(?ims)^\s*\*\*{Regex.Escape(label)}:\*\*\s*(?<value>.*?)(?=^\s*\*\*[^*]+:\*\*|\z)");
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    internal static string? ParseRecommendation(string markdown)
    {
        Match explicitValue = RecommendedDispositionRegex().Match(markdown);
        string candidate = explicitValue.Success
            ? explicitValue.Groups["value"].Value
            : markdown.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        candidate = candidate.Trim().Trim('*', '_', '`', ' ', '—', '-', ':', '.');

        if (Regex.IsMatch(candidate, @"\bexisting\b", RegexOptions.IgnoreCase))
        {
            return "existing";
        }
        if (Regex.IsMatch(candidate, @"\b(?:Disposition|Proposal)\s*A\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(candidate, @"^(?:A)\b", RegexOptions.IgnoreCase))
        {
            return "A";
        }
        if (Regex.IsMatch(candidate, @"\b(?:Disposition|Proposal)\s*B\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(candidate, @"^(?:B)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(candidate, @"\balternative\b", RegexOptions.IgnoreCase))
        {
            return "B";
        }
        if (Regex.IsMatch(candidate, @"\b(?:Disposition|Proposal)\s*C\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(candidate, @"^(?:C)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(candidate, @"\bdecline\b", RegexOptions.IgnoreCase))
        {
            return "C";
        }

        return null;
    }

    internal static string ExtractRecommendationJustification(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        Match match = RecommendedDispositionRegex().Match(markdown);
        if (match.Success)
        {
            string remainder = markdown[(match.Index + match.Length)..].Trim();
            return string.IsNullOrWhiteSpace(remainder) ? markdown.Trim() : remainder;
        }

        string[] lines = markdown.Split(['\r', '\n']);
        int firstContent = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        if (firstContent >= 0 && lines[firstContent].TrimStart().StartsWith("**", StringComparison.Ordinal))
        {
            string remainder = string.Join('\n', lines[(firstContent + 1)..]).Trim();
            return string.IsNullOrWhiteSpace(remainder) ? lines[firstContent].Trim() : remainder;
        }

        return markdown.Trim();
    }

    [GeneratedRegex(
        @"(?im)^\s*\*\*(?:Recommended disposition|Overall recommended disposition):\*\*\s*(?<value>[^\r\n]+)")]
    private static partial Regex RecommendedDispositionRegex();
}
