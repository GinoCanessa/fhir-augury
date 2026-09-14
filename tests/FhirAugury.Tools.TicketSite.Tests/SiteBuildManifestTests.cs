using FhirAugury.Publishing.Tickets;
using FhirAugury.Publishing.Tickets.Tests;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;

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
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: PreparedTicketSnapshotSchemaV2.Version);
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
        Assert.Equal(
            PreparedTicketSnapshotSchemaV2.Version,
            manifest.SnapshotSchemaVersion);
        Assert.Equal("Discussion tickets", manifest.Title);
        Assert.Equal(
            "Discussion tickets - Built September 08, 2026 " +
            "(filtered: spec=FHIR, project=FHIR, wg=FHIR Infrastructure)",
            manifest.DisplayTitle);
        Assert.Equal(
            new DateTimeOffset(
                2026,
                9,
                8,
                5,
                0,
                0,
                TimeSpan.Zero),
            manifest.JiraSourceLastSuccessfulRefreshAt);
        Assert.Equal(2, manifest.RendererSchemaVersion);
        Assert.NotNull(manifest.DiscussionReadiness);
        Assert.False(manifest.DiscussionReadiness.IsReady);
        Assert.Contains(
            manifest.DiscussionReadiness.Reasons,
            reason => reason.Code ==
                DiscussionPublicationReadinessReasonCodes
                    .LegacySnapshotSchema);
        Assert.Contains("site_metadata", manifest.TableCounts.Keys);
        Assert.Contains("ticket_people", manifest.TableCounts.Keys);
        Assert.Contains(
            snapshot.Descriptor.SnapshotId,
            stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            "Resolved --spec 'fhir' → 'FHIR'.",
            stdout,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(output, "index.html")));
        string chooser = await File.ReadAllTextAsync(
            Path.Combine(output, "index.html"));
        Assert.Contains(
            "Discussion tickets - Built September 08, 2026 " +
            "(filtered: spec=FHIR, project=FHIR, wg=FHIR Infrastructure)",
            chooser,
            StringComparison.Ordinal);
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
        TicketSiteManifest manifest = TicketSiteManifest.Read(Path.Combine(
            output,
            "applying",
            TicketSiteManifest.FileName));
        Assert.Equal("Ticket Site", manifest.Title);
        Assert.Equal(1, manifest.SnapshotSchemaVersion);
        Assert.Null(manifest.DisplayTitle);
        Assert.Null(manifest.JiraSourceLastSuccessfulRefreshAt);
        Assert.Null(manifest.RendererSchemaVersion);
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
