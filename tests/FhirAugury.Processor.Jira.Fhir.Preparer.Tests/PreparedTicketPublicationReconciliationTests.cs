using System.Text.Json;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketPublicationReconciliationTests
{
    [Fact]
    public void ContractSerializesFrozenDecisionsCountsAndRecoveryState()
    {
        DateTimeOffset capturedAt =
            new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
        PreparedTicketPublicationReconciliationItemDecision decision = new(
            "FHIR-13054",
            PreparedTicketPublicationReconciliationDispositionValues.ReAuthor,
            "revision-1",
            "revision-2",
            "receipt-1",
            "item-1",
            "run-1",
            Hash('a'),
            Hash('b'));
        PreparedTicketPublicationReconciliationComparison comparison = new(
            PreparedTicketPublicationReconciliationContract.CurrentVersion,
            "source-run",
            "source-snapshot",
            Hash('c'),
            "jira-generation-42",
            capturedAt,
            Hash('d'),
            [decision]);
        AuthoringRunReconciliationCounts counts = new(1, 0, 1);
        PreparedTicketPublicationReconciliationPromotionStatus promotion =
            new(
                PreparedTicketPublicationReconciliationPromotionStateValues
                    .SnapshotPublishPending,
                "database-promoted",
                true,
                capturedAt);
        PreparedTicketPublicationReconciliationProof proof = new(
            1,
            PreparedTicketPublicationReconciliationContract.Purpose,
            "source-run",
            "source-snapshot",
            "jira-generation-42",
            1,
            0,
            1,
            Hash('d'),
            Hash('e'),
            capturedAt);

        string json = JsonSerializer.Serialize(
            new { comparison, counts, promotion, proof },
            JsonSerializerOptions.Web);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(
            "re-author",
            root.GetProperty("comparison")
                .GetProperty("items")[0]
                .GetProperty("disposition")
                .GetString());
        Assert.Equal(
            "jira-generation-42",
            root.GetProperty("comparison")
                .GetProperty("stableJiraGeneration")
                .GetString());
        Assert.Equal(
            "snapshot-publish-pending",
            root.GetProperty("promotion").GetProperty("state").GetString());
        Assert.True(root.GetProperty("promotion")
            .GetProperty("mutationFenceHeld").GetBoolean());
        Assert.Equal(
            "publication-reconciliation",
            root.GetProperty("proof").GetProperty("purpose").GetString());
        Assert.Equal(
            "publication-refresh",
            PreparedTicketPublicationContract.PublicationRefreshPurpose);
    }

    [Theory]
    [InlineData("carry-forward")]
    [InlineData("re-author")]
    public void DispositionValuesAreStable(string disposition)
    {
        Assert.True(
            PreparedTicketPublicationReconciliationDispositionValues
                .IsValid(disposition));
        PreparedTicketPublicationReconciliationDispositionValues.EnsureValid(
            disposition);
    }

    [Fact]
    public void FailureCodesAreStableAndUnknownCodesRemainUnknown()
    {
        string[] codes =
        [
            PreparedTicketPublicationReconciliationFailureCodes.InvalidBaseline,
            PreparedTicketPublicationReconciliationFailureCodes.UnstableJiraGeneration,
            PreparedTicketPublicationReconciliationFailureCodes.RevisionInvalidation,
            PreparedTicketPublicationReconciliationFailureCodes.StagingMismatch,
            PreparedTicketPublicationReconciliationFailureCodes.GroupingImpactMismatch,
            PreparedTicketPublicationReconciliationFailureCodes.RecoveryInProgress,
            PreparedTicketPublicationReconciliationFailureCodes.PromotionRecoveryFailure,
            PreparedTicketPublicationReconciliationFailureCodes.CanonicalUnpublishedRestriction,
        ];

        Assert.Equal(
            [
                "invalid-baseline",
                "unstable-jira-generation",
                "revision-invalidation",
                "staging-mismatch",
                "grouping-impact-mismatch",
                "recovery-in-progress",
                "promotion-recovery-failure",
                "canonical-unpublished-restriction",
            ],
            codes);
        Assert.All(
            codes,
            code => Assert.True(
                PreparedTicketPublicationReconciliationFailureCodes.IsKnown(
                    code)));
        Assert.False(
            PreparedTicketPublicationReconciliationFailureCodes.IsKnown(
                "source-revision-mismatch"));
    }

    [Fact]
    public void PromotionStatesIncludeOnlyAuditableLifecycleStates()
    {
        Assert.True(
            PreparedTicketPublicationReconciliationPromotionStateValues.IsValid(
                "staged"));
        Assert.True(
            PreparedTicketPublicationReconciliationPromotionStateValues.IsValid(
                "snapshot-publish-pending"));
        Assert.True(
            PreparedTicketPublicationReconciliationPromotionStateValues.IsValid(
                "ready"));
        Assert.True(
            PreparedTicketPublicationReconciliationPromotionStateValues.IsValid(
                "canonical-unpublished"));
        Assert.False(
            PreparedTicketPublicationReconciliationPromotionStateValues.IsValid(
                "abandoned"));
    }

    private static string Hash(char value) => new(value, 64);
}
