namespace FhirAugury.Tools.TicketSite.Tests;

[Collection("ConsoleRedirect")]
public sealed class ChooserAndCliTests
{
    [Fact]
    public async Task CliRejectsMissingSnapshotInput()
    {
        (int exit, string error) = await RunAsync("--out", "unused");

        Assert.Equal(2, exit);
        Assert.Contains("Specify either --preparer-snapshot or --planner-snapshot", error);
    }

    [Theory]
    [InlineData("--preparer-db")]
    [InlineData("--planner-db")]
    [InlineData("--jira-source")]
    [InlineData("--jira-source-db")]
    public async Task CliRejectsRetiredLiveInputOptions(string option)
    {
        (int exit, string error) = await RunAsync(option, "retired");

        Assert.Equal(2, exit);
        Assert.Contains($"Unknown argument: {option}", error);
    }

    [Fact]
    public async Task CliSnapshotRequiresDescriptor()
    {
        (int exit, string error) = await RunAsync(
            "--preparer-snapshot",
            "snapshot.db");

        Assert.Equal(2, exit);
        Assert.Contains("--snapshot-descriptor", error);
    }

    [Fact]
    public async Task PreparerSnapshotBuildsDiscussionAndChooser()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"ticket-site-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            TicketSnapshotFixture snapshot =
                await TicketSnapshotFixture.CreatePreparerAsync(root);
            string output = Path.Combine(root, "site");

            int exit = await Program.Main([
                "--preparer-snapshot", snapshot.DatabasePath,
                "--snapshot-descriptor", snapshot.DescriptorPath,
                "--out", output,
            ]);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(Path.Combine(output, "discussion", "index.html")));
            Assert.True(File.Exists(Path.Combine(output, "index.html")));
            Assert.False(Directory.Exists(Path.Combine(output, "applying")));
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PreparerTopicWorkGroupFilterUsesDisplayName()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"ticket-site-topic-filter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string output = Path.Combine(root, "discussion");
            PreparerSubSiteEmitter.Emit(
                output,
                "Tickets",
                ResolvedFilters.None,
                [1]);

            string script = await File.ReadAllTextAsync(
                Path.Combine(output, "assets", "app.js"));

            Assert.Contains(
                "pushInList('t.WorkGroupDisplay', wgValues)",
                script,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "pushInList('t.WorkGroupClean', wgValues)",
                script,
                StringComparison.Ordinal);
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task PlannerSnapshotBuildsApplyingAndChooser()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"ticket-site-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            TicketSnapshotFixture snapshot =
                await TicketSnapshotFixture.CreatePlannerAsync(root);
            string output = Path.Combine(root, "site");

            int exit = await Program.Main([
                "--planner-snapshot", snapshot.DatabasePath,
                "--snapshot-descriptor", snapshot.DescriptorPath,
                "--out", output,
            ]);

            Assert.Equal(0, exit);
            Assert.True(File.Exists(Path.Combine(output, "applying", "index.html")));
            Assert.True(File.Exists(Path.Combine(output, "index.html")));
            Assert.False(Directory.Exists(Path.Combine(output, "discussion")));
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }

    [Fact]
    public void OutputDirGuardKindMatchesDetectsMismatch()
    {
        MetaFilterSet existing = new()
        {
            Kind = PreparerSubSiteEmitter.Kind,
            Filters = new MetaFilters(),
        };

        Assert.True(OutputDirGuard.KindMatches(existing, PreparerSubSiteEmitter.Kind));
        Assert.False(OutputDirGuard.KindMatches(existing, PlannerSubSiteEmitter.Kind));
    }

    private static async Task<(int Exit, string Error)> RunAsync(
        params string[] args)
    {
        TextWriter originalError = Console.Error;
        StringWriter error = new();
        Console.SetError(error);
        try
        {
            return (await Program.Main(args), error.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
