namespace FhirAugury.Publishing.Tickets.Tests;

public class WorkGroupResolverParseTests
{
    [Fact]
    public async Task SnapshotCatalogResolvesCodeNameAndCleanName()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"wg-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            TicketSnapshotFixture snapshot =
                await TicketSnapshotFixture.CreatePreparerAsync(root);
            Assert.Equal(
                "FHIR Infrastructure",
                await WorkGroupResolver.TryResolveAsync(
                    "fhir-i",
                    snapshot.DatabasePath,
                    CancellationToken.None));
            Assert.Equal(
                "FHIR Infrastructure",
                await WorkGroupResolver.TryResolveAsync(
                    "FHIRInfrastructure",
                    snapshot.DatabasePath,
                    CancellationToken.None));
            Assert.Equal(
                "FHIR Infrastructure",
                await WorkGroupResolver.TryResolveAsync(
                    "FHIR Infrastructure",
                    snapshot.DatabasePath,
                    CancellationToken.None));
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }
}
