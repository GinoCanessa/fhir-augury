using FhirAugury.Common.Api;
using FhirAugury.Common.Text;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

public sealed class PreparedTicketPublicationEnricher(
    OrchestratorHydrationFetcher fetcher,
    ILogger<PreparedTicketPublicationEnricher> logger)
{
    public async Task<PreparedTicketPublicationEnrichmentBatch> FetchAsync(
        PreparedTicketPublicationEnrichmentInput input,
        CancellationToken ct = default)
    {
        _ = PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(input);
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        IReadOnlyList<PreparedTicketPublicationMetadata> jira =
            await FetchJiraMetadataAsync(
                PreparedTicketPublicationRefreshInventory.FromEnrichmentInput(input).Candidates,
                hydratedAt,
                ct);
        Dictionary<string, HydrationZulipRow> lookups = new(StringComparer.Ordinal);
        List<PreparedTicketPublicationZulipOutcome> outcomes = new(input.ZulipReferences.Count);
        foreach (PreparedTicketPublicationZulipReference association in input.ZulipReferences)
        {
            ct.ThrowIfCancellationRequested();
            if (!lookups.TryGetValue(association.Reference, out HydrationZulipRow? result))
            {
                result = await fetcher.FetchZulipAsync(
                    association.TicketKey, association.Reference, hydratedAt, ct);
                if (!string.Equals(result.TicketKey, association.TicketKey, StringComparison.OrdinalIgnoreCase) ||
                    result.ZulipThreadId != association.Reference)
                {
                    throw new PreparedTicketPublicationRefreshStageException(
                        PreparedTicketPublicationRefreshFailureCodes.InvalidEnrichmentBatch,
                        "The Zulip lookup returned a different accepted association.");
                }
                lookups.Add(association.Reference, result);
            }

            ZulipReferenceHydrationReasonReadResult reason =
                ZulipReferenceHydrationReason.Read(result.HydrationReason);
            if (reason.Metadata?.LatestOutcome != ZulipReferenceLookupOutcome.Resolved)
            {
                logger.LogWarning(
                    "Publication enrichment lookup for ticket {TicketKey}, association {AssociationId} at {AttemptedAt}: {Outcome}; metadata diagnostic {MetadataDiagnostic}",
                    association.TicketKey, association.AssociationId, hydratedAt,
                    reason.Metadata?.LatestOutcome, reason.MetadataFailure);
            }
            outcomes.Add(new(
                association.AssociationId,
                result with { TicketKey = association.TicketKey }));
        }
        return new(jira, outcomes);
    }

    internal async Task<IReadOnlyList<PreparedTicketPublicationMetadata>> FetchJiraMetadataAsync(
        IReadOnlyList<PreparedTicketPublicationRefreshCandidate> candidates,
        DateTimeOffset hydratedAt,
        CancellationToken ct)
    {
        List<PreparedTicketPublicationMetadata> metadata = new(candidates.Count);
        long? sourceContentRevision = null;
        foreach (PreparedTicketPublicationRefreshCandidate candidate in candidates)
        {
            PublicationMetadataFetchResult result =
                await fetcher.FetchPublicationMetadataAsync(candidate.TicketKey, hydratedAt, ct);
            if (!result.IsSuccess)
            {
                PublicationMetadataFetchFailure failure = result.Failure
                    ?? throw new InvalidOperationException("A failed metadata result has no failure.");
                if (failure.Reason == PublicationMetadataFetchFailureReason.TicketNotFound)
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.SourceRevisionMismatch,
                        $"{PreparedTicketPublicationRefreshFailureCodes.TicketNotFound}: Jira source ticket '{candidate.TicketKey}' no longer exists. {failure.Detail}");
                }
                throw new PreparedTicketPublicationRefreshStageException(
                    ToFailureCode(failure.Reason), $"{candidate.TicketKey}: {failure.Detail}");
            }
            if (!string.Equals(result.TicketKey, candidate.TicketKey, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(result.ObservedSourceRevision) ||
                result.SourceProject is null ||
                result.SourceLastSuccessfulRefreshAt is null ||
                result.SourceContentRevision is null or < 0 ||
                result.SourceIsStable != true ||
                result.PublicDisplayNamePolicyVersion != PublicDisplayNamePolicy.CurrentVersion)
            {
                throw new PreparedTicketPublicationRefreshStageException(
                    PreparedTicketPublicationRefreshFailureCodes.InvalidSourceResponse,
                    $"{candidate.TicketKey}: The successful publication metadata response was incomplete.");
            }

            if (!JiraSourceRevision.AreEquivalent(candidate.ExpectedSourceRevision, result.ObservedSourceRevision))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.SourceRevisionMismatch,
                    $"{PreparedTicketPublicationRefreshFailureCodes.SourceRevisionMismatch}: Jira source revision for '{candidate.TicketKey}' changed from '{candidate.ExpectedSourceRevision}' to '{result.ObservedSourceRevision}'.");
            }
            if (sourceContentRevision is not null && sourceContentRevision != result.SourceContentRevision)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.SourceRevisionMismatch,
                    $"{PreparedTicketPublicationRefreshFailureCodes.SourceGenerationConflict}: Publication metadata spans more than one Jira content revision.");
            }
            sourceContentRevision = result.SourceContentRevision;

            metadata.Add(new(
                candidate.TicketKey,
                result.ObservedSourceRevision,
                result.Reporter,
                result.Assignee,
                result.InPersonRequesters,
                result.SourceProject,
                result.SourceLastSuccessfulRefreshAt.Value,
                result.SourceContentRevision.Value,
                true,
                PublicDisplayNamePolicy.CurrentVersion,
                result.HydratedAt,
                result.UpdatedAt));
        }
        return metadata;
    }

    private static string ToFailureCode(PublicationMetadataFetchFailureReason reason)
        => reason switch
        {
            PublicationMetadataFetchFailureReason.SourceUnavailable =>
                PreparedTicketPublicationRefreshFailureCodes.SourceUnavailable,
            PublicationMetadataFetchFailureReason.TicketNotFound =>
                PreparedTicketPublicationRefreshFailureCodes.TicketNotFound,
            PublicationMetadataFetchFailureReason.MissingSourceProvenance =>
                PreparedTicketPublicationRefreshFailureCodes.MissingSourceProvenance,
            PublicationMetadataFetchFailureReason.UnstableSource =>
                PreparedTicketPublicationRefreshFailureCodes.UnstableSource,
            PublicationMetadataFetchFailureReason.MissingProjectProvenance =>
                PreparedTicketPublicationRefreshFailureCodes.MissingProjectProvenance,
            PublicationMetadataFetchFailureReason.PeoplePolicyNotCurrent =>
                PreparedTicketPublicationRefreshFailureCodes.PeoplePolicyNotCurrent,
            _ => PreparedTicketPublicationRefreshFailureCodes.InvalidSourceResponse,
        };
}
