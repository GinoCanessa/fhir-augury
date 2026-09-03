using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

namespace FhirAugury.Tools.TicketMdToDb.Compilation;

public enum ImportedValueState
{
    Present,
    Missing,
    Ambiguous,
}

public enum SourceRangeDisposition
{
    Mapped,
    DeliberatelyMerged,
    IntentionallyUnmapped,
}

public sealed record SourceEvidence(
    string SourcePath,
    string SourceSha256,
    string HeadingPath,
    int StartOffset,
    int EndOffset,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string MappingRuleId,
    string Markdown);

public sealed record ClassifiedSourceRange(
    int StartOffset,
    int EndOffset,
    SourceRangeDisposition Disposition,
    string Reason,
    SourceEvidence Evidence);

public sealed record ImportedScalar(
    string Field,
    ImportedValueState State,
    string? Value,
    IReadOnlyList<SourceEvidence> Evidence)
{
    public static ImportedScalar Present(string field, string value, params SourceEvidence[] evidence) =>
        new(field, ImportedValueState.Present, value, evidence);

    public static ImportedScalar Missing(string field) =>
        new(field, ImportedValueState.Missing, null, []);

    public static ImportedScalar Ambiguous(string field, string value, params SourceEvidence[] evidence) =>
        new(field, ImportedValueState.Ambiguous, value, evidence);
}

public sealed record ImportedRepoCandidate(
    string Repo,
    string RepoCategory,
    string Justification,
    IReadOnlyList<SourceEvidence> Evidence);

public sealed record ImportedJiraCandidate(
    string JiraKey,
    string LinkType,
    string Justification,
    IReadOnlyList<SourceEvidence> Evidence);

public sealed record ImportedZulipCandidate(
    string ThreadId,
    string Justification,
    IReadOnlyList<SourceEvidence> Evidence);

public sealed record ImportedGitHubCandidate(
    string ItemId,
    string Justification,
    bool UnsupportedCommit,
    IReadOnlyList<SourceEvidence> Evidence);

public sealed class ImportedTicketReview
{
    public required string Key { get; init; }
    public required string DialectId { get; set; }
    public required DiscoveredReport Source { get; init; }
    public Dictionary<string, ImportedScalar> Scalars { get; } = new(StringComparer.Ordinal);
    public List<ImportedRepoCandidate> Repositories { get; } = [];
    public List<ImportedJiraCandidate> RelatedJira { get; } = [];
    public List<ImportedZulipCandidate> RelatedZulip { get; } = [];
    public List<ImportedGitHubCandidate> RelatedGitHub { get; } = [];
    public List<ClassifiedSourceRange> SourceRanges { get; } = [];
    public List<ImportDiagnostic> Diagnostics { get; } = [];

    public ImportedScalar GetScalar(string field) =>
        Scalars.TryGetValue(field, out ImportedScalar? value)
            ? value
            : ImportedScalar.Missing(field);
}

public static class PreparedTicketFieldNames
{
    public const string RequestSummary = nameof(PreparedTicketPayload.RequestSummary);
    public const string CommentSummary = nameof(PreparedTicketPayload.CommentSummary);
    public const string LinkedTicketSummary = nameof(PreparedTicketPayload.LinkedTicketSummary);
    public const string RelatedTicketSummary = nameof(PreparedTicketPayload.RelatedTicketSummary);
    public const string RelatedZulipSummary = nameof(PreparedTicketPayload.RelatedZulipSummary);
    public const string RelatedGitHubSummary = nameof(PreparedTicketPayload.RelatedGitHubSummary);
    public const string ExistingProposed = nameof(PreparedTicketPayload.ExistingProposed);
    public const string ProposalA = nameof(PreparedTicketPayload.ProposalA);
    public const string ProposalAJustification = nameof(PreparedTicketPayload.ProposalAJustification);
    public const string ProposalAImpact = nameof(PreparedTicketPayload.ProposalAImpact);
    public const string ProposalB = nameof(PreparedTicketPayload.ProposalB);
    public const string ProposalBJustification = nameof(PreparedTicketPayload.ProposalBJustification);
    public const string ProposalBImpact = nameof(PreparedTicketPayload.ProposalBImpact);
    public const string ProposalC = nameof(PreparedTicketPayload.ProposalC);
    public const string ProposalCJustification = nameof(PreparedTicketPayload.ProposalCJustification);
    public const string Recommendation = nameof(PreparedTicketPayload.Recommendation);
    public const string RecommendationJustification = nameof(PreparedTicketPayload.RecommendationJustification);

    public static IReadOnlySet<string> Overrideable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        RequestSummary,
        CommentSummary,
        LinkedTicketSummary,
        RelatedTicketSummary,
        RelatedZulipSummary,
        RelatedGitHubSummary,
        ExistingProposed,
        ProposalA,
        ProposalAJustification,
        ProposalAImpact,
        ProposalB,
        ProposalBJustification,
        ProposalBImpact,
        ProposalC,
        ProposalCJustification,
        Recommendation,
        RecommendationJustification,
    };
}
