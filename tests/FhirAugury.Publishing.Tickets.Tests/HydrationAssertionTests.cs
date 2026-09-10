using FhirAugury.Processing.Client;

namespace FhirAugury.Publishing.Tickets.Tests;

public sealed class HydrationAssertionTests
{
    [Fact]
    public async Task SnapshotValidationRejectsDescriptorProcessorMismatch()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"hydration-descriptor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            TicketSnapshotFixture snapshot =
                await TicketSnapshotFixture.CreatePreparerAsync(root);
            await TicketSnapshotFixture.WriteDescriptorAsync(
                snapshot.DescriptorPath,
                snapshot.Descriptor with { ProcessorKind = "wrong-processor" });

            InvalidOperationException exception = await Assert.ThrowsAsync<
                InvalidOperationException>(
                () => snapshot.CreateVerifiedPairAsync("Preparer"));

            Assert.Contains(
                "processor",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SnapshotValidationAcceptsStateBackedHistoricalLedger()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"hydration-historical-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            TicketSnapshotFixture snapshot =
                await TicketSnapshotFixture.CreatePreparerAsync(
                    root,
                    includeSecondTicket: true);
            await snapshot.MoveSecondTicketToHistoricalLedgerAsync();

            VerifiedAuthoringSnapshotPair pair =
                await snapshot.CreateVerifiedPairAsync("Preparer");
            TicketSitePublishResult result =
                await new TicketSitePublisher().PublishAsync(
                    new TicketSitePublishRequest(
                        pair,
                        TicketSiteKind.Discussion,
                        Path.Combine(root, "site"),
                        "Tickets"));

            Assert.Equal(2, result.Manifest.IncludedReceiptCount);
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }
}
