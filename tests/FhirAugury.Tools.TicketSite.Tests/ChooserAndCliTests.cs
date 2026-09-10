using FhirAugury.Publishing.Tickets.Tests;

namespace FhirAugury.Tools.TicketSite.Tests;

[Collection("ConsoleRedirect")]
public sealed class ChooserAndCliTests
{
    [Fact]
    public async Task CliRejectsMissingSnapshotInput()
    {
        (int exit, _, string error) = await RunAsync("--out", "unused");

        Assert.Equal(2, exit);
        Assert.Contains(
            "Specify either --preparer-snapshot or --planner-snapshot",
            error);
    }

    [Theory]
    [InlineData("--preparer-db")]
    [InlineData("--planner-db")]
    [InlineData("--jira-source")]
    [InlineData("--jira-source-db")]
    public async Task CliRejectsRetiredLiveInputOptions(string option)
    {
        (int exit, _, string error) = await RunAsync(option, "retired");

        Assert.Equal(2, exit);
        Assert.Contains($"Unknown argument: {option}", error);
    }

    [Fact]
    public async Task CliSnapshotRequiresDescriptor()
    {
        (int exit, _, string error) = await RunAsync(
            "--preparer-snapshot",
            "snapshot.db");

        Assert.Equal(2, exit);
        Assert.Contains("--snapshot-descriptor", error);
    }

    [Fact]
    public async Task HelpRetainsEverySupportedFlag()
    {
        (int exit, string output, string error) = await RunAsync("--help");

        Assert.Equal(0, exit);
        Assert.Empty(error);
        string[] flags =
        [
            "--preparer-snapshot",
            "--planner-snapshot",
            "--snapshot-descriptor",
            "--out",
            "--title",
            "--spec",
            "--project",
            "--wg",
            "--force",
            "--help",
        ];
        foreach (string flag in flags)
        {
            Assert.Contains(flag, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LooseSnapshotRejectsSidecarCreatedAfterInitialCheck()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"ticket-site-late-sidecar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            TicketSnapshotFixture snapshot =
                await TicketSnapshotFixture.CreatePreparerAsync(root);
            string output = Path.Combine(root, "site");
            bool captureStarted = false;
            TicketSiteCliTestHooks hooks = new(
                BeforeLegacySnapshotCaptureAsync: (databasePath, _) =>
                {
                    captureStarted = true;
                    File.WriteAllText(databasePath + "-wal", "late sidecar");
                    return Task.CompletedTask;
                });

            (int exit, _, string error) = await RunWithHooksAsync(
                hooks,
                "--preparer-snapshot", snapshot.DatabasePath,
                "--snapshot-descriptor", snapshot.DescriptorPath,
                "--out", output);

            Assert.True(captureStarted);
            Assert.Equal(1, exit);
            Assert.Contains("sidecar", error, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(output, "discussion")));
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(root);
        }
    }

    internal static async Task<(int Exit, string Output, string Error)> RunAsync(
        params string[] args)
        => await RunCoreAsync(args, testHooks: null).ConfigureAwait(false);

    private static async Task<(int Exit, string Output, string Error)>
        RunWithHooksAsync(
            TicketSiteCliTestHooks testHooks,
            params string[] args)
        => await RunCoreAsync(args, testHooks).ConfigureAwait(false);

    private static async Task<(int Exit, string Output, string Error)> RunCoreAsync(
        string[] args,
        TicketSiteCliTestHooks? testHooks)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        StringWriter output = new();
        StringWriter error = new();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            return (
                await Program.RunAsync(args, testHooks: testHooks),
                output.ToString(),
                error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
