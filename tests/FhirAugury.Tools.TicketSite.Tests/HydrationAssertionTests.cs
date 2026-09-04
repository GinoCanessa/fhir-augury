namespace FhirAugury.Tools.TicketSite.Tests;

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

            using StringWriter stderr = new();
            HydrationAssertion.SnapshotValidationResult? result =
                await HydrationAssertion.ValidateSnapshotAsync(
                    snapshot.DatabasePath,
                    snapshot.DescriptorPath,
                    PreparerSubSiteEmitter.Kind,
                    stderr,
                    CancellationToken.None);

            Assert.Null(result);
            Assert.Contains("processor kind", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
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

            using StringWriter stderr = new();
            HydrationAssertion.SnapshotValidationResult? result =
                await HydrationAssertion.ValidateSnapshotAsync(
                    snapshot.DatabasePath,
                    snapshot.DescriptorPath,
                    PreparerSubSiteEmitter.Kind,
                    stderr,
                    CancellationToken.None);

            Assert.True(result is not null, stderr.ToString());
            Assert.Equal(2, result!.Descriptor.ReceiptCount);
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }
}
