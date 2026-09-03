using FhirAugury.Tools.TicketMdToDb.Mapping;

namespace FhirAugury.Tools.TicketMdToDb.Compilation;

internal abstract class LegacyTicketReviewDialectBase(string key, string id) : ITicketReviewDialect
{
    protected string Key { get; } = key;
    public string Id { get; } = id;

    public abstract bool Recognizes(TicketReviewDocument document);
    public abstract ImportedTicketReview Parse(TicketReviewDocument document);

    protected ImportedTicketReview Create(TicketReviewDocument document) => new()
    {
        Key = document.Source.Key,
        DialectId = Id,
        Source = document.Source,
    };

    protected bool IsExpectedKey(TicketReviewDocument document) =>
        string.Equals(document.Source.Key, Key, StringComparison.Ordinal);

    protected static void MapSimpleDisposition(
        ImportedTicketReview review,
        TicketReviewDocument document,
        string headingPrefix,
        string suffix,
        string rulePrefix)
    {
        TicketReviewSection? section = document.FindSectionStartingWith(headingPrefix);
        string body = document.Body(section);
        string proposal = CanonicalTicketReviewDialect.ExtractHeadingOrBoldField(
            body,
            "Proposal",
            "Justification");
        if (string.IsNullOrWhiteSpace(proposal))
        {
            proposal = body;
        }

        string justification = CanonicalTicketReviewDialect.ExtractHeadingOrBoldField(
            body,
            "Justification",
            null);
        if (string.IsNullOrWhiteSpace(justification))
        {
            justification = CanonicalTicketReviewDialect.ExtractBoldField(body, "Rationale");
        }
        if (string.IsNullOrWhiteSpace(justification))
        {
            justification = body;
        }

        TicketReviewDialectHelpers.SetScalar(
            review,
            document,
            $"Proposal{suffix}",
            proposal,
            section,
            $"{rulePrefix}-proposal");
        TicketReviewDialectHelpers.SetScalar(
            review,
            document,
            $"Proposal{suffix}Justification",
            justification,
            section,
            $"{rulePrefix}-justification");
    }

    protected static void AddClassifications(
        ImportedTicketReview review,
        TicketReviewDocument document,
        IEnumerable<string> mappedHeadings,
        IEnumerable<string>? intentionallyUnmapped = null)
    {
        Dictionary<string, (SourceRangeDisposition, string)> classifications =
            mappedHeadings.ToDictionary(
                TicketReviewDocument.NormalizeHeading,
                heading => (SourceRangeDisposition.Mapped, $"legacy-mapped-{heading.ToLowerInvariant().Replace(' ', '-')}"),
                StringComparer.OrdinalIgnoreCase);
        foreach (string heading in intentionallyUnmapped ?? [])
        {
            classifications[TicketReviewDocument.NormalizeHeading(heading)] =
                (SourceRangeDisposition.IntentionallyUnmapped, $"legacy-unmapped-{heading.ToLowerInvariant().Replace(' ', '-')}");
        }

        review.SourceRanges.AddRange(document.ClassifyTopLevelSections(
            classifications,
            "legacy-noncontract-section"));
    }
}

internal sealed class LegacyTicketReviewDialect10333()
    : LegacyTicketReviewDialectBase("FHIR-10333", "legacy-fhir-10333")
{
    public override bool Recognizes(TicketReviewDocument document) =>
        IsExpectedKey(document)
        && document.HasOrderedHeadings("Executive Summary", "Ticket Details", "Disposition Options", "Recommended Disposition");

    public override ImportedTicketReview Parse(TicketReviewDocument document)
    {
        ImportedTicketReview review = Create(document);
        TicketReviewSection? summary = document.FindSection("Executive Summary");
        TicketReviewSection? details = document.FindSection("Ticket Details");
        TicketReviewSection? linked = document.FindSectionStartingWith("Linked Jira Tickets");
        TicketReviewSection? related = document.FindSectionStartingWith("Related/Similar Jira Tickets");
        TicketReviewSection? zulip = document.FindSection("Related Zulip Discussions");
        TicketReviewSection? github = document.FindSectionStartingWith("Related GitHub");
        TicketReviewSection? repos = document.FindSectionStartingWith("Repository Context");

        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RequestSummary, summary, "10333-summary");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.CommentSummary, details, "10333-details");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.LinkedTicketSummary, linked, "10333-linked");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RelatedTicketSummary, related, "10333-related");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RelatedZulipSummary, zulip, "10333-zulip");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RelatedGitHubSummary, github, "10333-github");
        MapSimpleDisposition(review, document, "Disposition A", "A", "10333-a");
        MapSimpleDisposition(review, document, "Disposition B", "B", "10333-b");
        MapSimpleDisposition(review, document, "Disposition C", "C", "10333-c");
        CanonicalTicketReviewDialect.MapRecommendation(
            review,
            document,
            document.FindSection("Recommended Disposition"),
            "10333-recommendation");
        RelatedReferenceMapper.Map(review, document, linked, related, zulip, github, repos);
        AddClassifications(
            review,
            document,
            [
                "Executive Summary",
                "Ticket Details",
                "Linked Jira Tickets (Explicitly Linked)",
                "Related/Similar Jira Tickets (Discovered)",
                "Related Zulip Discussions",
                "Related GitHub Issues and Pull Requests",
                "Repository Context",
                "Disposition Options",
                "Recommended Disposition",
            ]);
        return review;
    }
}

internal sealed class LegacyTicketReviewDialect10654()
    : LegacyTicketReviewDialectBase("FHIR-10654", "legacy-fhir-10654")
{
    public override bool Recognizes(TicketReviewDocument document) =>
        IsExpectedKey(document)
        && document.HasOrderedHeadings("Ticket Summary", "Detailed Analysis", "Change Request / Implementation Option");

    public override ImportedTicketReview Parse(TicketReviewDocument document)
    {
        ImportedTicketReview review = Create(document);
        TicketReviewSection? summary = document.FindSection("Discussion Summary");
        TicketReviewSection? original = document.FindSection("Original Ticket Text (quoted)");
        TicketReviewSection? related = document.FindSection("Related Discussions and Tickets");
        TicketReviewSection? proposed = document.FindSection("Proposed Disposition");
        TicketReviewSection? changeRequest = document.FindSection("Change Request / Implementation Option");
        string proposal = string.Join(
            "\n\n",
            new[] { document.Body(proposed), document.Body(changeRequest) }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RequestSummary, summary, "10654-summary");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.CommentSummary, original, "10654-original");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RelatedTicketSummary, related, "10654-related");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RelatedZulipSummary, related, "10654-zulip");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RelatedGitHubSummary, related, "10654-github");
        TicketReviewDialectHelpers.SetScalar(review, document, PreparedTicketFieldNames.ExistingProposed, document.Body(proposed), proposed, "10654-existing");
        TicketReviewDialectHelpers.SetScalar(review, document, PreparedTicketFieldNames.ProposalA, proposal, changeRequest ?? proposed, "10654-proposal-a");
        TicketReviewDialectHelpers.SetScalar(review, document, PreparedTicketFieldNames.ProposalAJustification, document.Body(proposed), proposed, "10654-proposal-a-justification");
        review.Scalars[PreparedTicketFieldNames.ProposalB] = ImportedScalar.Missing(PreparedTicketFieldNames.ProposalB);
        review.Scalars[PreparedTicketFieldNames.ProposalC] = ImportedScalar.Missing(PreparedTicketFieldNames.ProposalC);
        CanonicalTicketReviewDialect.MapRecommendation(
            review,
            document,
            proposed,
            "10654-recommendation",
            "A");
        RelatedReferenceMapper.Map(review, document, related, related, related, related, related);
        AddClassifications(
            review,
            document,
            [
                "Original Ticket Text (quoted)",
                "Detailed Analysis",
                "Change Request / Implementation Option",
                "Related Discussions and Tickets",
            ],
            ["Ticket Summary", "Related Artifacts/Pages", "Open Questions"]);
        return review;
    }
}

internal sealed class LegacyTicketReviewDialect12563()
    : LegacyTicketReviewDialectBase("FHIR-12563", "legacy-fhir-12563")
{
    public override bool Recognizes(TicketReviewDocument document) =>
        IsExpectedKey(document)
        && document.HasOrderedHeadings("Summary", "Details", "Proposed Dispositions")
        && document.Markdown.Contains("**Proposal:**", StringComparison.Ordinal);

    public override ImportedTicketReview Parse(TicketReviewDocument document)
    {
        ImportedTicketReview review = Create(document);
        TicketReviewSection? summary = document.FindSection("Summary");
        TicketReviewSection? details = document.FindSection("Details");
        TicketReviewSection? linked = document.FindSection("Linked Jira Tickets");
        TicketReviewSection? related = document.FindSection("Related Jira Tickets");
        TicketReviewSection? zulip = document.FindSection("Related Zulip Discussions");
        TicketReviewSection? github = document.FindSection("Related GitHub Items");
        TicketReviewSection? repos = document.FindSection("Repo Context");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RequestSummary, summary, "12563-summary");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.CommentSummary, details, "12563-details");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.LinkedTicketSummary, linked, "12563-linked");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RelatedTicketSummary, related, "12563-related");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RelatedZulipSummary, zulip, "12563-zulip");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RelatedGitHubSummary, github, "12563-github");
        MapSimpleDisposition(review, document, "Disposition A", "A", "12563-a");
        MapSimpleDisposition(review, document, "Disposition B", "B", "12563-b");
        MapSimpleDisposition(review, document, "Disposition C", "C", "12563-c");
        CanonicalTicketReviewDialect.MapRecommendation(
            review,
            document,
            document.FindSectionStartingWith("Recommendation"),
            "12563-recommendation",
            "existing");
        RelatedReferenceMapper.Map(review, document, linked, related, zulip, github, repos);
        AddClassifications(
            review,
            document,
            [
                "Summary",
                "Details",
                "Linked Jira Tickets",
                "Related Jira Tickets",
                "Related Zulip Discussions",
                "Related GitHub Items",
                "Repo Context",
                "Proposed Dispositions",
            ],
            ["Keywords"]);
        return review;
    }
}

internal sealed class LegacyTicketReviewDialect13634()
    : LegacyTicketReviewDialectBase("FHIR-13634", "legacy-fhir-13634")
{
    public override bool Recognizes(TicketReviewDocument document) =>
        IsExpectedKey(document)
        && document.HasOrderedHeadings(
            "Summary",
            "Details",
            "Keywords",
            "Linked Jira Tickets",
            "Related Jira Tickets",
            "Related Zulip Discussions",
            "Related GitHub Items",
            "Repo Context",
            "Proposed Dispositions")
        && document.Markdown.Contains("Recommended disposition:** alternative", StringComparison.OrdinalIgnoreCase);

    public override ImportedTicketReview Parse(TicketReviewDocument document)
    {
        ImportedTicketReview review = new CanonicalTicketReviewDialect().Parse(document);
        review.DialectId = Id;
        TicketReviewSection? recommendation = document.FindSectionStartingWith("Recommendation");
        TicketReviewDialectHelpers.SetScalar(
            review,
            document,
            PreparedTicketFieldNames.Recommendation,
            "B",
            recommendation,
            "13634-alternative-alias");
        return review;
    }
}

internal sealed class LegacyTicketReviewDialect16662()
    : LegacyTicketReviewDialectBase("FHIR-16662", "legacy-fhir-16662")
{
    public override bool Recognizes(TicketReviewDocument document) =>
        IsExpectedKey(document)
        && document.HasOrderedHeadings("Ticket Summary", "Proposed Dispositions", "Recommendation")
        && document.Markdown.Contains("### Proposal A", StringComparison.Ordinal);

    public override ImportedTicketReview Parse(TicketReviewDocument document)
    {
        ImportedTicketReview review = Create(document);
        TicketReviewSection? summary = document.FindSection("Ticket Summary");
        TicketReviewSection? description = document.FindSection("Description");
        TicketReviewSection? comments = document.FindSection("Comments");
        TicketReviewSection? linked = document.FindSection("Linked Jira Tickets");
        TicketReviewSection? related = document.FindSection("Related Jira Tickets");
        TicketReviewSection? zulip = document.FindSection("Related Zulip Discussions");
        TicketReviewSection? github = document.FindSectionStartingWith("Related GitHub");
        TicketReviewSection? repos = document.FindSection("Repo Context");
        string details = string.Join(
            "\n\n",
            new[] { document.Body(description), document.Body(comments) }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RequestSummary, summary, "16662-summary");
        TicketReviewDialectHelpers.SetScalar(review, document, PreparedTicketFieldNames.CommentSummary, details, description ?? comments, "16662-details");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.LinkedTicketSummary, linked, "16662-linked");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RelatedTicketSummary, related, "16662-related");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RelatedZulipSummary, zulip, "16662-zulip");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RelatedGitHubSummary, github, "16662-github");
        MapSimpleDisposition(review, document, "Proposal A", "A", "16662-a");
        MapSimpleDisposition(review, document, "Proposal B", "B", "16662-b");
        MapSimpleDisposition(review, document, "Proposal C", "C", "16662-c");
        CanonicalTicketReviewDialect.MapRecommendation(
            review,
            document,
            document.FindSection("Recommendation"),
            "16662-recommendation");
        RelatedReferenceMapper.Map(review, document, linked, related, zulip, github, repos);
        AddClassifications(
            review,
            document,
            [
                "Ticket Summary",
                "Description",
                "Comments",
                "Linked Jira Tickets",
                "Related Jira Tickets",
                "Related Zulip Discussions",
                "Related GitHub Activity",
                "Repo Context",
                "Proposed Dispositions",
                "Recommendation",
            ],
            ["Key Themes"]);
        return review;
    }
}

internal sealed class LegacyTicketReviewDialect17156()
    : LegacyTicketReviewDialectBase("FHIR-17156", "legacy-fhir-17156")
{
    public override bool Recognizes(TicketReviewDocument document) =>
        IsExpectedKey(document)
        && document.HasOrderedHeadings("Ticket Details", "Summary", "Potential Dispositions", "Recommendation")
        && document.Markdown.Contains("### A. Accept as requested", StringComparison.Ordinal);

    public override ImportedTicketReview Parse(TicketReviewDocument document)
    {
        ImportedTicketReview review = Create(document);
        TicketReviewSection? summary = document.FindSection("Summary");
        TicketReviewSection? description = document.FindSection("Description");
        TicketReviewSection? comments = document.FindSection("Comments");
        TicketReviewSection? state = document.FindSection("Current State Summary");
        TicketReviewSection? linked = document.FindSection("Linked Jira Tickets");
        TicketReviewSection? related = document.FindSection("Related Jira Tickets");
        TicketReviewSection? zulip = document.FindSectionStartingWith("Related Zulip Discussion");
        TicketReviewSection? github = document.FindSection("Related GitHub Items");
        TicketReviewSection? repos = document.FindSection("Repo Analysis");
        string details = string.Join(
            "\n\n",
            new[] { document.Body(description), document.Body(comments), document.Body(state) }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RequestSummary, summary, "17156-summary");
        TicketReviewDialectHelpers.SetScalar(review, document, PreparedTicketFieldNames.CommentSummary, details, description ?? comments ?? state, "17156-details");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.LinkedTicketSummary, linked, "17156-linked");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RelatedTicketSummary, related, "17156-related");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RelatedZulipSummary, zulip, "17156-zulip");
        TicketReviewDialectHelpers.SetSectionScalar(review, document, PreparedTicketFieldNames.RelatedGitHubSummary, github, "17156-github");
        MapSimpleDisposition(review, document, "A. Accept as requested", "A", "17156-a");
        MapSimpleDisposition(review, document, "B. Fix the publication mechanism", "B", "17156-b");
        MapSimpleDisposition(review, document, "Decline", "C", "17156-c");
        TicketReviewSection? recommendation = document.FindSection("Recommendation");
        CanonicalTicketReviewDialect.MapRecommendation(
            review,
            document,
            recommendation,
            "17156-recommendation",
            "A");
        RelatedReferenceMapper.Map(review, document, linked, related, zulip, github, repos);
        AddClassifications(
            review,
            document,
            [
                "Description",
                "Summary",
                "Linked Jira Tickets",
                "Related Jira Tickets",
                "Related Zulip Discussion",
                "Related GitHub Items",
                "Comments",
                "Current State Summary",
                "Potential Dispositions",
                "Recommendation",
                "Repo Analysis",
            ],
            ["Ticket Details", "Related URLs", "Users Involved", "Keywords"]);
        return review;
    }
}
