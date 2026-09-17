using System.Globalization;

namespace FhirAugury.Publishing.Tickets;

public static class DiscussionPublicationReadinessReasonCodes
{
    public const string LegacySnapshotSchema = "legacy-snapshot-schema";
    public const string MissingOrdinaryProvenance =
        "missing-ordinary-provenance";
    public const string InvalidRefreshProof = "invalid-refresh-proof";
    public const string InvalidReconciliationProof =
        "invalid-reconciliation-proof";
    public const string InvalidCanonicalEpochRecoveryProof =
        "invalid-canonical-epoch-recovery-proof";
    public const string MissingPeoplePolicyProof =
        "missing-people-policy-proof";
}

public static class DiscussionPublicationReadinessEvidence
{
    public const string OrdinarySnapshot = "ordinary-snapshot";
    public const string PublicationRefresh = "publication-refresh";
    public const string PublicationReconciliation =
        "publication-reconciliation";
    public const string CanonicalEpochRecovery =
        "canonical-epoch-recovery";
}

public sealed record DiscussionPublicationReadinessReason(
    string Code,
    string Message);

public sealed record DiscussionPublicationReadiness(
    bool IsReady,
    string Evidence,
    long? JiraSourceContentRevision,
    int? PublicDisplayNamePolicyVersion,
    IReadOnlyList<DiscussionPublicationReadinessReason> Reasons)
{
    internal static DiscussionPublicationReadiness Create(
        string evidence,
        long? jiraSourceContentRevision,
        int? publicDisplayNamePolicyVersion,
        IEnumerable<string> reasonCodes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        ArgumentNullException.ThrowIfNull(reasonCodes);
        DiscussionPublicationReadinessReason[] reasons = reasonCodes
            .Distinct(StringComparer.Ordinal)
            .Select(CreateReason)
            .OrderBy(reason => ReasonOrder(reason.Code))
            .ToArray();
        return new DiscussionPublicationReadiness(
            reasons.Length == 0,
            evidence,
            jiraSourceContentRevision,
            publicDisplayNamePolicyVersion,
            Array.AsReadOnly(reasons));
    }

    internal static DiscussionPublicationReadiness Unqualified { get; } =
        Create(
            DiscussionPublicationReadinessEvidence.OrdinarySnapshot,
            jiraSourceContentRevision: null,
            publicDisplayNamePolicyVersion: null,
            [
                DiscussionPublicationReadinessReasonCodes
                    .MissingOrdinaryProvenance,
                DiscussionPublicationReadinessReasonCodes
                    .MissingPeoplePolicyProof,
            ]);

    private static DiscussionPublicationReadinessReason CreateReason(
        string code)
        => code switch
        {
            DiscussionPublicationReadinessReasonCodes.LegacySnapshotSchema =>
                new(
                    code,
                    "The source snapshot predates the current schema-v3 publication contract."),
            DiscussionPublicationReadinessReasonCodes
                    .MissingOrdinaryProvenance =>
                new(
                    code,
                    "Complete receipt-backed Jira source provenance is unavailable."),
            DiscussionPublicationReadinessReasonCodes.InvalidRefreshProof =>
                new(
                    code,
                    "The publication-refresh proof does not match this snapshot's corpus and grouping output."),
            DiscussionPublicationReadinessReasonCodes
                    .InvalidReconciliationProof =>
                new(
                    code,
                    "The publication-reconciliation proof does not match this ready snapshot's corpus and grouping output."),
            DiscussionPublicationReadinessReasonCodes
                    .InvalidCanonicalEpochRecoveryProof =>
                new(
                    code,
                    "The canonical-epoch-recovery proof does not match this ready snapshot's complete corpus and grouping output."),
            DiscussionPublicationReadinessReasonCodes
                    .MissingPeoplePolicyProof =>
                new(
                    code,
                    "Current public-display-name policy proof is unavailable for one or more tickets."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(code),
                code,
                "Unknown discussion publication-readiness reason code."),
        };

    private static int ReasonOrder(string code)
        => code switch
        {
            DiscussionPublicationReadinessReasonCodes.LegacySnapshotSchema => 0,
            DiscussionPublicationReadinessReasonCodes
                .MissingOrdinaryProvenance => 1,
            DiscussionPublicationReadinessReasonCodes.InvalidRefreshProof => 2,
            DiscussionPublicationReadinessReasonCodes
                .InvalidReconciliationProof => 3,
            DiscussionPublicationReadinessReasonCodes
                .InvalidCanonicalEpochRecoveryProof => 4,
            DiscussionPublicationReadinessReasonCodes
                .MissingPeoplePolicyProof => 5,
            _ => int.MaxValue,
        };
}

internal sealed record TicketSitePresentation(
    int RendererSchemaVersion,
    string BaseTitle,
    string SiteName,
    DateTimeOffset? JiraSourceLastSuccessfulRefreshAt,
    ResolvedFilters Filters,
    DiscussionPublicationReadiness Readiness,
    DiscussionCorpusSummary CorpusSummary)
{
    private static readonly string[] MonthLabels =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sept", "Oct", "Nov", "Dec"];

    public static TicketSitePresentation CreateDiscussion(
        string baseTitle,
        DateTimeOffset? jiraSourceLastSuccessfulRefreshAt,
        ResolvedFilters filters,
        DiscussionCorpusSummary corpusSummary,
        DiscussionPublicationReadiness? readiness = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseTitle);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(corpusSummary);

        DateTimeOffset? utcRefresh =
            jiraSourceLastSuccessfulRefreshAt?.ToUniversalTime();
        string dateSuffix = string.Empty;
        if (corpusSummary.TicketCount > 0 &&
            corpusSummary.ValidJiraUpdatedAtCount == corpusSummary.TicketCount &&
            corpusSummary.DateCoverage == DiscussionDateCoverage.Complete &&
            corpusSummary.MaxJiraUpdatedAt is DateTimeOffset maximum)
        {
            DateTimeOffset utcDate = maximum.ToUniversalTime();
            dateSuffix = $" - {MonthLabels[utcDate.Month - 1]} " +
                utcDate.Day.ToString(CultureInfo.InvariantCulture) + ", " +
                utcDate.Year.ToString("D4", CultureInfo.InvariantCulture);
        }
        return new TicketSitePresentation(
            DiscussionRendererSchema.Version,
            baseTitle,
            $"{baseTitle}{dateSuffix}{filters.ToTitleSuffix()}",
            utcRefresh,
            filters,
            readiness ?? DiscussionPublicationReadiness.Unqualified,
            corpusSummary);
    }
}
