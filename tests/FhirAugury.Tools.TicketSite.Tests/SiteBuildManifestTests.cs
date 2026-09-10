using FhirAugury.Publishing.Tickets;
using FhirAugury.Publishing.Tickets.Tests;

namespace FhirAugury.Tools.TicketSite.Tests;

[Collection("ConsoleRedirect")]
public sealed class SiteBuildManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"ticket-site-cli-{Guid.NewGuid():N}");

    public SiteBuildManifestTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_root);

    [Fact]
    public async Task PreparerInvocationWritesCompatibleSummaryAndChooser()
    {
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        string output = Path.Combine(_root, "site");

        (int exit, string stdout, string stderr) =
            await ChooserAndCliTests.RunAsync(
                "--preparer-snapshot", snapshot.DatabasePath,
                "--snapshot-descriptor", snapshot.DescriptorPath,
                "--out", output,
                "--title", "Discussion tickets",
                "--spec", "fhir",
                "--project", "fhir",
                "--wg", "fhir-i",
                "--force");

        Assert.True(exit == 0, stderr);
        TicketSiteManifest manifest = TicketSiteManifest.Read(Path.Combine(
            output,
            "discussion",
            TicketSiteManifest.FileName));
        Assert.Equal(snapshot.Descriptor.SnapshotId, manifest.SnapshotId);
        Assert.Equal("Discussion tickets", manifest.Title);
        Assert.Contains(
            snapshot.Descriptor.SnapshotId,
            stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            "Resolved --spec 'fhir' → 'FHIR'.",
            stdout,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(output, "index.html")));
    }

    [Fact]
    public async Task PlannerInvocationWritesApplyingSite()
    {
        TicketSnapshotFixture snapshot =
            await TicketSnapshotFixture.CreatePlannerAsync(_root);
        string output = Path.Combine(_root, "planner-site");

        (int exit, string stdout, string stderr) =
            await ChooserAndCliTests.RunAsync(
                "--planner-snapshot", snapshot.DatabasePath,
                "--snapshot-descriptor", snapshot.DescriptorPath,
                "--out", output);

        Assert.True(exit == 0, stderr);
        Assert.Contains(
            snapshot.Descriptor.SnapshotId,
            stdout,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            output,
            "applying",
            "index.html")));
        Assert.True(File.Exists(Path.Combine(output, "index.html")));
        Assert.False(Directory.Exists(Path.Combine(output, "discussion")));
    }
}
