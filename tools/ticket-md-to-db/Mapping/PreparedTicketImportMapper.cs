using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Tools.TicketMdToDb.Compilation;
using FhirAugury.Tools.TicketMdToDb.Overrides;

namespace FhirAugury.Tools.TicketMdToDb.Mapping;

public sealed record PreparedTicketMappingResult(
    ManifestTicket Ticket,
    IReadOnlyList<ImportDiagnostic> Diagnostics);

public static class PreparedTicketImportMapper
{
    public const string RequiredMissingText = "Not present in source Markdown";

    private static readonly HashSet<string> RequiredTextFields = new(StringComparer.Ordinal)
    {
        PreparedTicketFieldNames.RequestSummary,
        PreparedTicketFieldNames.ProposalA,
        PreparedTicketFieldNames.ProposalB,
        PreparedTicketFieldNames.ProposalC,
        PreparedTicketFieldNames.RecommendationJustification,
    };

    public static PreparedTicketMappingResult Map(
        ImportedTicketReview review,
        ImportOverrideEntry? ticketOverride,
        int documentLength)
    {
        List<ImportDiagnostic> diagnostics = [];
        ValidateRangeCoverage(review, documentLength, diagnostics);
        List<AppliedImportOverride> appliedOverrides = [];
        List<ManifestScalar> scalars = [];

        string Resolve(string field)
        {
            ImportedScalar source = review.GetScalar(field);
            if (ticketOverride?.Fields.TryGetValue(field, out string? overridden) == true)
            {
                appliedOverrides.Add(new AppliedImportOverride(
                    review.Key,
                    field,
                    source.Value,
                    overridden,
                    ticketOverride.SourceSha256,
                    ticketOverride.ReviewReason));
                scalars.Add(new ManifestScalar(
                    field,
                    source.State,
                    source.Value,
                    overridden,
                    source.Evidence));
                return overridden;
            }

            string applied;
            if (source.State == ImportedValueState.Present)
            {
                applied = source.Value ?? string.Empty;
            }
            else if (field is PreparedTicketFieldNames.ProposalAImpact or PreparedTicketFieldNames.ProposalBImpact)
            {
                applied = PreparedTicketImpactValues.NotAssessed;
            }
            else if (field == PreparedTicketFieldNames.Recommendation)
            {
                applied = string.Empty;
                diagnostics.Add(ImportDiagnostic.Blocking(
                    source.State == ImportedValueState.Ambiguous
                        ? "recommendation-ambiguous"
                        : "recommendation-missing",
                    $"Ticket {review.Key} requires an explicit recommendation or reviewed override.",
                    review.Source.RelativePath,
                    review.Key));
            }
            else if (RequiredTextFields.Contains(field))
            {
                applied = RequiredMissingText;
            }
            else
            {
                applied = string.Empty;
            }

            scalars.Add(new ManifestScalar(
                field,
                source.State,
                source.Value,
                applied,
                source.Evidence));
            return applied;
        }

        PreparedTicketPayload payload = new()
        {
            Key = review.Key,
            RequestSummary = Resolve(PreparedTicketFieldNames.RequestSummary),
            CommentSummary = Resolve(PreparedTicketFieldNames.CommentSummary),
            LinkedTicketSummary = Resolve(PreparedTicketFieldNames.LinkedTicketSummary),
            RelatedTicketSummary = Resolve(PreparedTicketFieldNames.RelatedTicketSummary),
            RelatedZulipSummary = Resolve(PreparedTicketFieldNames.RelatedZulipSummary),
            RelatedGitHubSummary = Resolve(PreparedTicketFieldNames.RelatedGitHubSummary),
            ExistingProposed = Resolve(PreparedTicketFieldNames.ExistingProposed),
            ProposalA = Resolve(PreparedTicketFieldNames.ProposalA),
            ProposalAJustification = Resolve(PreparedTicketFieldNames.ProposalAJustification),
            ProposalAImpact = Resolve(PreparedTicketFieldNames.ProposalAImpact),
            ProposalB = Resolve(PreparedTicketFieldNames.ProposalB),
            ProposalBJustification = Resolve(PreparedTicketFieldNames.ProposalBJustification),
            ProposalBImpact = Resolve(PreparedTicketFieldNames.ProposalBImpact),
            ProposalC = Resolve(PreparedTicketFieldNames.ProposalC),
            ProposalCJustification = Resolve(PreparedTicketFieldNames.ProposalCJustification),
            Recommendation = Resolve(PreparedTicketFieldNames.Recommendation),
            RecommendationJustification = Resolve(PreparedTicketFieldNames.RecommendationJustification),
            SavedAt = null,
            Repos = DeduplicateRepositories(review.Repositories),
            RelatedJiraTickets = DeduplicateJira(review.RelatedJira),
            RelatedZulipThreads = DeduplicateZulip(review.RelatedZulip),
            RelatedGitHubItems = DeduplicateGitHub(review.RelatedGitHub),
        };

        foreach (string error in PreparedTicketPayloadValidator.Validate(payload))
        {
            diagnostics.Add(ImportDiagnostic.Blocking(
                "payload-validation-failed",
                error,
                review.Source.RelativePath,
                review.Key));
        }

        ManifestTicket ticket = new(
            review.Key,
            review.DialectId,
            review.Source.RelativePath,
            review.Source.SourceSha256,
            payload,
            scalars.OrderBy(scalar => scalar.Field, StringComparer.Ordinal).ToArray(),
            BuildChildEvidence(review),
            review.SourceRanges.OrderBy(range => range.StartOffset).ToArray(),
            appliedOverrides.OrderBy(item => item.Field, StringComparer.Ordinal).ToArray());
        return new PreparedTicketMappingResult(ticket, diagnostics);
    }

    private static IReadOnlyList<ManifestChildEvidence> BuildChildEvidence(
        ImportedTicketReview review)
    {
        List<ManifestChildEvidence> children = [];
        foreach (IGrouping<string, ImportedRepoCandidate> group in review.Repositories.GroupBy(
                     candidate => candidate.Repo.Trim().TrimEnd('/'),
                     StringComparer.OrdinalIgnoreCase))
        {
            children.Add(CreateChild(
                "repository",
                group.First().Repo.Trim().TrimEnd('/'),
                null,
                false,
                group.Select(candidate => candidate.Justification),
                group.SelectMany(candidate => candidate.Evidence)));
        }
        foreach (IGrouping<string, ImportedJiraCandidate> group in review.RelatedJira.GroupBy(
                     candidate => $"{candidate.JiraKey.ToUpperInvariant()}\u001f{candidate.LinkType.ToLowerInvariant()}",
                     StringComparer.Ordinal))
        {
            ImportedJiraCandidate first = group.First();
            children.Add(CreateChild(
                "jira",
                first.JiraKey.ToUpperInvariant(),
                first.LinkType.ToLowerInvariant(),
                false,
                group.Select(candidate => candidate.Justification),
                group.SelectMany(candidate => candidate.Evidence)));
        }
        foreach (IGrouping<string, ImportedZulipCandidate> group in review.RelatedZulip.GroupBy(
                     candidate => candidate.ThreadId,
                     StringComparer.Ordinal))
        {
            children.Add(CreateChild(
                "zulip",
                group.Key,
                null,
                false,
                group.Select(candidate => candidate.Justification),
                group.SelectMany(candidate => candidate.Evidence)));
        }
        foreach (IGrouping<string, ImportedGitHubCandidate> group in review.RelatedGitHub.GroupBy(
                     candidate => candidate.ItemId,
                     StringComparer.OrdinalIgnoreCase))
        {
            children.Add(CreateChild(
                "github",
                group.First().ItemId,
                null,
                group.Any(candidate => candidate.UnsupportedCommit),
                group.Select(candidate => candidate.Justification),
                group.SelectMany(candidate => candidate.Evidence)));
        }

        return children
            .OrderBy(child => child.Kind, StringComparer.Ordinal)
            .ThenBy(child => child.Identity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(child => child.LinkType ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
    }

    private static ManifestChildEvidence CreateChild(
        string kind,
        string identity,
        string? linkType,
        bool unsupported,
        IEnumerable<string> justifications,
        IEnumerable<SourceEvidence> evidence)
    {
        string[] normalizedJustifications = justifications
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        SourceEvidence[] normalizedEvidence = evidence
            .DistinctBy(item => (
                item.SourcePath,
                item.StartOffset,
                item.EndOffset,
                item.MappingRuleId))
            .OrderBy(item => item.SourcePath, StringComparer.Ordinal)
            .ThenBy(item => item.StartOffset)
            .ThenBy(item => item.EndOffset)
            .ThenBy(item => item.MappingRuleId, StringComparer.Ordinal)
            .ToArray();
        return new ManifestChildEvidence(
            kind,
            identity,
            linkType,
            unsupported,
            normalizedJustifications,
            normalizedEvidence);
    }

    private static void ValidateRangeCoverage(
        ImportedTicketReview review,
        int documentLength,
        List<ImportDiagnostic> diagnostics)
    {
        if (documentLength == 0)
        {
            return;
        }

        ClassifiedSourceRange[] ranges = review.SourceRanges
            .OrderBy(range => range.StartOffset)
            .ThenBy(range => range.EndOffset)
            .ToArray();
        if (ranges.Length == 0)
        {
            diagnostics.Add(ImportDiagnostic.Blocking(
                "source-range-unaccounted",
                "Dialect did not classify any source ranges.",
                review.Source.RelativePath,
                review.Key));
            return;
        }

        int expectedStart = 0;
        foreach (ClassifiedSourceRange range in ranges)
        {
            if (range.StartOffset > expectedStart)
            {
                diagnostics.Add(ImportDiagnostic.Blocking(
                    "source-range-unaccounted",
                    $"Source offsets {expectedStart}-{range.StartOffset - 1} are unaccounted.",
                    review.Source.RelativePath,
                    review.Key));
            }
            else if (range.StartOffset < expectedStart)
            {
                diagnostics.Add(ImportDiagnostic.Blocking(
                    "source-range-multiply-consumed",
                    $"Source range beginning at {range.StartOffset} overlaps a prior classification.",
                    review.Source.RelativePath,
                    review.Key));
            }

            expectedStart = Math.Max(expectedStart, range.EndOffset + 1);
        }

        if (expectedStart < documentLength)
        {
            diagnostics.Add(ImportDiagnostic.Blocking(
                "source-range-unaccounted",
                $"Source offsets {expectedStart}-{documentLength - 1} are unaccounted.",
                review.Source.RelativePath,
                review.Key));
        }
    }

    private static List<PreparedTicketRepoPayload> DeduplicateRepositories(
        IReadOnlyList<ImportedRepoCandidate> candidates)
    {
        List<PreparedTicketRepoPayload> rows = [];
        foreach (IGrouping<string, ImportedRepoCandidate> group in candidates
                     .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Repo))
                     .GroupBy(
                         candidate => candidate.Repo.Trim().TrimEnd('/'),
                         StringComparer.OrdinalIgnoreCase))
        {
            ImportedRepoCandidate first = group.First();
            rows.Add(new PreparedTicketRepoPayload
            {
                Repo = first.Repo.Trim().TrimEnd('/'),
                RepoCategory = group.Select(candidate => candidate.RepoCategory)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    ?? string.Empty,
                Justification = MergeJustifications(group.Select(candidate => candidate.Justification)),
            });
        }

        return rows;
    }

    private static List<PreparedTicketRelatedJiraPayload> DeduplicateJira(
        IReadOnlyList<ImportedJiraCandidate> candidates)
    {
        List<PreparedTicketRelatedJiraPayload> rows = [];
        foreach (IGrouping<string, ImportedJiraCandidate> group in candidates
                     .GroupBy(
                         candidate => $"{candidate.JiraKey.ToUpperInvariant()}\u001f{candidate.LinkType.ToLowerInvariant()}",
                         StringComparer.Ordinal))
        {
            ImportedJiraCandidate first = group.First();
            rows.Add(new PreparedTicketRelatedJiraPayload
            {
                AssociatedTicketKey = first.JiraKey.ToUpperInvariant(),
                LinkType = first.LinkType.ToLowerInvariant(),
                Justification = MergeJustifications(group.Select(candidate => candidate.Justification)),
            });
        }

        return rows;
    }

    private static List<PreparedTicketRelatedZulipPayload> DeduplicateZulip(
        IReadOnlyList<ImportedZulipCandidate> candidates)
    {
        List<PreparedTicketRelatedZulipPayload> rows = [];
        foreach (IGrouping<string, ImportedZulipCandidate> group in candidates
                     .GroupBy(candidate => candidate.ThreadId, StringComparer.Ordinal))
        {
            ImportedZulipCandidate first = group.First();
            rows.Add(new PreparedTicketRelatedZulipPayload
            {
                ZulipThreadId = first.ThreadId,
                Justification = MergeJustifications(group.Select(candidate => candidate.Justification)),
            });
        }

        return rows;
    }

    private static List<PreparedTicketRelatedGitHubPayload> DeduplicateGitHub(
        IReadOnlyList<ImportedGitHubCandidate> candidates)
    {
        List<PreparedTicketRelatedGitHubPayload> rows = [];
        foreach (IGrouping<string, ImportedGitHubCandidate> group in candidates
                     .GroupBy(candidate => candidate.ItemId, StringComparer.OrdinalIgnoreCase))
        {
            ImportedGitHubCandidate first = group.First();
            rows.Add(new PreparedTicketRelatedGitHubPayload
            {
                GitHubItemId = first.ItemId,
                Justification = MergeJustifications(group.Select(candidate => candidate.Justification)),
            });
        }

        return rows;
    }

    private static string MergeJustifications(IEnumerable<string> values)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> merged = [];
        foreach (string value in values)
        {
            string trimmed = value.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) && seen.Add(trimmed))
            {
                merged.Add(trimmed);
            }
        }

        return string.Join("\n\n", merged);
    }
}
