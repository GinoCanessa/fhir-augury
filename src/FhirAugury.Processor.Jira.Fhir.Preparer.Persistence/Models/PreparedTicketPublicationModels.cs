using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processing.Contracts;

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
