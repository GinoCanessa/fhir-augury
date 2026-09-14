using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;

public sealed record PreparedTicketPublicationRefreshCandidate(
    string TicketKey,
    string ReceiptId,
    string RunItemId,
    string ContributingRunId,
    string ItemKind,
    string ExpectedSourceRevision)
{
    public PreparedTicketPublicationCorpusItem ToPublicationCorpusItem()
        => new(
            TicketKey,
            ReceiptId,
            RunItemId,
            ContributingRunId,
            ItemKind,
            ExpectedSourceRevision);
}

public sealed record PreparedTicketPublicationRefreshInventory(
    IReadOnlyList<PreparedTicketPublicationRefreshCandidate> Candidates,
    string CorpusFingerprint);

public sealed record PreparedTicketPublicationMetadata(
    string TicketKey,
    string ObservedSourceRevision,
    string? Reporter,
    string? Assignee,
    IReadOnlyList<string> InPersonRequesters,
    string SourceProject,
    DateTimeOffset SourceLastSuccessfulRefreshAt,
    long SourceContentRevision,
    bool SourceIsStable,
    int PublicDisplayNamePolicyVersion,
    DateTimeOffset HydratedAt);

public sealed record PreparedTicketGroupingReceiptCoordinate(
    string RunId,
    string StageId,
    string PartitionKey,
    string InputFingerprint,
    string? OutputFingerprint,
    DateTimeOffset PersistedAt);

public sealed record PreparedTicketGroupingCertificationEvidence(
    string RefreshRunId,
    string PartitionKey,
    string InputFingerprint,
    string OutputFingerprint,
    PreparedTicketGroupingReceiptCoordinate SourceReceipt,
    bool IsLegacyCertification,
    DateTimeOffset CertifiedAt);
