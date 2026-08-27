using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Verifies that <see cref="GhCliRunner"/> treats caller cancellation as cancellation:
/// it kills and reaps the child process, releases the concurrency gate, and throws a
/// token-linked <see cref="OperationCanceledException"/> rather than a
/// <see cref="TimeoutException"/> or a raw <c>TaskCanceledException</c>.
/// </summary>
public class GhCliRunnerCancellationTests
{
    /// <summary>Process name used for the child-kill assertion (no extension).</summary>
    private static string PingProcessName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "PING" : "ping";

    /// <summary>
    /// Builds an executable + arguments pair that blocks for ~15s then outputs [].
    /// Long enough that the process cannot exit on its own before the cancel assertion.
    /// </summary>
    private static (string Executable, string Args) BuildDelayCommand()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("cmd", "/c \"ping -n 15 127.0.0.1 >nul & echo []\"");
        }

        return ("bash", "-c \"ping -c 15 -i 1 127.0.0.1 >/dev/null 2>&1; echo '[]'\"");
    }

    /// <summary>Builds an executable + arguments pair that returns [] immediately.</summary>
    private static (string Executable, string Args) BuildFastCommand()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("cmd", "/c \"echo []\"");
        }

        return ("bash", "-c \"echo '[]'\"");
    }

    private static GhCliRunner CreateRunner(
        string executable,
        int maxConcurrentProcesses = 1,
        string processTimeout = "00:00:30")
    {
        IOptions<GitHubServiceOptions> options = Options.Create(new GitHubServiceOptions
        {
            GhCli = new GhCliConfiguration
            {
                ExecutablePath = executable,
                MaxConcurrentProcesses = maxConcurrentProcesses,
                ProcessTimeout = processTimeout,
            },
        });

        return new GhCliRunner(options, NullLogger<GhCliRunner>.Instance);
    }

    private static int[] SnapshotPingProcessIds()
    {
        try
        {
            return Process.GetProcessesByName(PingProcessName).Select(p => p.Id).ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// A cancelled call must surface as an <see cref="OperationCanceledException"/> carrying
    /// the caller's token — the shape <c>catch (OperationCanceledException) when (ct.IsCancellationRequested)</c>
    /// guards downstream depend on.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenTokenCanceled_ThrowsOperationCanceledException()
    {
        (string executable, string args) = BuildDelayCommand();
        GhCliRunner runner = CreateRunner(executable);

        using CancellationTokenSource cts = new CancellationTokenSource();
        Task<JsonDocument> call = runner.RunAsync(args, cts.Token);

        await Task.Delay(300);
        await cts.CancelAsync();

        OperationCanceledException ex =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);

        Assert.IsNotType<TimeoutException>(ex);
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    /// <summary>
    /// The pre-fix code returned promptly on cancel but orphaned the child, so a fast return
    /// proves nothing. This asserts the spawned child is actually gone afterwards.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenTokenCanceled_KillsChildProcess()
    {
        (string executable, string args) = BuildDelayCommand();
        GhCliRunner runner = CreateRunner(executable);

        int[] before = SnapshotPingProcessIds();

        using CancellationTokenSource cts = new CancellationTokenSource();
        Task<JsonDocument> call = runner.RunAsync(args, cts.Token);

        // Give the shell time to actually spawn the ping child.
        await Task.Delay(1500);

        int[] during = SnapshotPingProcessIds();
        int[] spawned = during.Except(before).ToArray();

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);

        if (spawned.Length == 0)
        {
            // Could not observe the child (platform/timing); the cancellation contract is
            // still covered by the other tests in this class.
            return;
        }

        int[] survivors = spawned;
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            survivors = SnapshotPingProcessIds().Intersect(spawned).ToArray();
            if (survivors.Length == 0)
                break;

            await Task.Delay(100);
        }

        Assert.True(
            survivors.Length == 0,
            $"Expected the cancelled child process(es) to be killed, but {survivors.Length} survived: " +
            string.Join(", ", survivors));
    }

    /// <summary>
    /// The child must be reaped before the process gate is released, so a
    /// <c>MaxConcurrentProcesses = 1</c> successor is not blocked behind a dying child.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenTokenCanceled_ReleasesGateForNextCall()
    {
        (string slowExecutable, string slowArgs) = BuildDelayCommand();
        (string _, string fastArgs) = BuildFastCommand();

        GhCliRunner runner = CreateRunner(slowExecutable, maxConcurrentProcesses: 1);

        using CancellationTokenSource cts = new CancellationTokenSource();
        Task<JsonDocument> slowCall = runner.RunAsync(slowArgs, cts.Token);

        await Task.Delay(300);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await slowCall);

        Task<JsonDocument> fastCall = runner.RunAsync(fastArgs, CancellationToken.None);
        Task winner = await Task.WhenAny(fastCall, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(
            ReferenceEquals(winner, fastCall),
            "Expected the process gate to be released after cancellation, but the next call did not complete within 5s.");

        using JsonDocument doc = await fastCall;
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    /// <summary>The timeout path must keep raising <see cref="TimeoutException"/>.</summary>
    [Fact]
    public async Task RunAsync_WhenProcessTimesOut_StillThrowsTimeoutException()
    {
        (string executable, string args) = BuildDelayCommand();
        GhCliRunner runner = CreateRunner(executable, processTimeout: "00:00:01");

        await Assert.ThrowsAsync<TimeoutException>(
            async () => await runner.RunAsync(args, CancellationToken.None));
    }

    /// <summary>
    /// Pins the documented precedence: the caller's token wins over the process timeout, so a
    /// shutdown is never reported as a timeout failure. The token fires while the 1s process
    /// timeout is armed and imminent — the window in which the pre-fix code either raised
    /// <see cref="TimeoutException"/> or leaked the linked (not caller) token.
    /// </summary>
    /// <remarks>
    /// True simultaneity cannot be forced deterministically, so the cancel is biased to land
    /// first; the classification branch under test (<c>ct.IsCancellationRequested</c> evaluated
    /// after kill + reap) is identical either way.
    /// </remarks>
    [Fact]
    public async Task RunAsync_WhenTimeoutAndCancelCoincide_PrefersCancellation()
    {
        (string executable, string args) = BuildDelayCommand();
        GhCliRunner runner = CreateRunner(executable, processTimeout: "00:00:01");

        using CancellationTokenSource cts = new CancellationTokenSource();
        Task<JsonDocument> call = runner.RunAsync(args, cts.Token);

        cts.CancelAfter(TimeSpan.FromMilliseconds(400));

        OperationCanceledException ex =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);

        Assert.IsNotType<TimeoutException>(ex);
        Assert.Equal(cts.Token, ex.CancellationToken);
    }
}
