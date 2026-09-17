using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

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
    string CorpusFingerprint)
{
    public static PreparedTicketPublicationRefreshInventory FromEnrichmentInput(
        PreparedTicketPublicationEnrichmentInput input)
        => new(
            input.Corpus.Select(item => new PreparedTicketPublicationRefreshCandidate(
                item.TicketKey, item.ReceiptId, item.RunItemId, item.ContributingRunId,
                item.ItemKind, item.ExpectedSourceRevision)).ToArray(),
            input.CorpusFingerprint);
}

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
    DateTimeOffset HydratedAt,
    DateTimeOffset? UpdatedAt = null);

public sealed record PreparedTicketPublicationZulipOutcome(
    string AssociationId,
    HydrationZulipRow Hydration);

public sealed record PreparedTicketPublicationEnrichmentBatch(
    IReadOnlyList<PreparedTicketPublicationMetadata> JiraMetadata,
    IReadOnlyList<PreparedTicketPublicationZulipOutcome> ZulipOutcomes);

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

public sealed record PreparedTicketPublicationProtectedGrouping(
    string PartitionKey,
    IReadOnlyList<PreparedTicketPublicationCorpusItem> Corpus,
    IReadOnlyList<PreparedTicketPublicationProtectedRow> Rows,
    PreparedTicketPublicationProtectedGroupingFingerprint Fingerprint);

public sealed record PreparedTicketPublicationProtectedInventory(
    int SchemaVersion,
    IReadOnlyList<PreparedTicketPublicationCorpusItem> Corpus,
    IReadOnlyList<PreparedTicketPublicationProtectedRow> Rows,
    IReadOnlyList<PreparedTicketPublicationProtectedGrouping> Grouping,
    IReadOnlyList<PreparedTicketPublicationZulipReference> ZulipReferences)
{
    public string CorpusFingerprint =>
        PreparedTicketPublicationContract.ComputeCorpusFingerprint(Corpus);

    public string ProtectedContentFingerprint =>
        PreparedTicketPublicationEnrichmentContract.ComputeProtectedContentFingerprint(Rows);

    public string RetainedGroupingFingerprint =>
        PreparedTicketPublicationEnrichmentContract.ComputeRetainedGroupingFingerprint(
            Grouping.Select(partition => partition.Fingerprint));
}

public sealed record PreparedTicketPublicationPreservationComparison(
    AuthoringRunCorpusComparison CorpusComparison,
    IReadOnlyList<string> AdditionalTicketKeys,
    IReadOnlyList<string> AdditionalGroupingPartitions,
    PreparedTicketPublicationProtectedInventory CurrentInventory);

public sealed record PreparedTicketPublicationBaseline(
    PreparedTicketPublicationEnrichmentSource Source,
    PreparedTicketPublicationProtectedInventory Inventory);

public sealed record PreparedTicketPublicationCarriedCoordinate(
    string TicketKey,
    string ReceiptId,
    string RunItemId,
    string ContributingRunId,
    string ItemKind,
    string ExpectedSourceRevision,
    string AuthoredFingerprint,
    string GroupingFingerprint);

public sealed record PreparedTicketPublicationStagedTicket(
    string RunId,
    string TicketKey,
    string RunItemId,
    string OperationId,
    string ReceiptId,
    string SourceRevision,
    string AuthoredFingerprint,
    string HydrationFingerprint,
    PreparedTicketPayload Payload,
    PreparedTicketHydrationBatch Hydration,
    DateTimeOffset StagedAt);

public sealed record PreparedTicketPublicationCorpusTicket(
    string TicketKey,
    string Disposition,
    string ReceiptId,
    string RunItemId,
    string ContributingRunId,
    string ItemKind,
    string ExpectedSourceRevision,
    string AuthoredFingerprint,
    PreparedTicketPayload Payload,
    PreparedTicketHydrationBatch Hydration)
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

public sealed record PreparedTicketPublicationCorpusOverlay(
    string RunId,
    string CorpusFingerprint,
    IReadOnlyList<PreparedTicketPublicationCorpusTicket> Tickets);

public sealed record PreparedTicketPublicationPromotionJournal(
    string RunId,
    string State,
    string? SnapshotDescriptorJson,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastRecoveryAttemptAt = null,
    string? FailureCode = null,
    string? FailureDetail = null);

public sealed record PreparedTicketPublicationStagedGroupingReplacement(
    string RunId,
    string PartitionKey,
    string ReplacementJson,
    string CorpusFingerprint,
    string OutputFingerprint,
    string ProtectedRowsFingerprint,
    DateTimeOffset StagedAt);

public sealed record PreparedTicketPublicationSnapshotDescriptor(
    string RunId,
    string DescriptorJson,
    string Sha256,
    DateTimeOffset PersistedAt);
