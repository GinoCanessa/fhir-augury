using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Tests;

public class GhCliRunnerConcurrencyTests
{
    /// <summary>
    /// Builds an executable + arguments pair that runs ping for ~2s then outputs [].
    /// On Windows: cmd /c "ping -n 3 127.0.0.1 >nul &amp; echo []"
    /// On Linux/macOS: ping runs directly with -c 3 -i 1.
    /// </summary>
    private static (string Executable, string Args) BuildDelayCommand()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("cmd", "/c \"ping -n 3 127.0.0.1 >nul & echo []\"");
        }
        else
        {
            // bash -c: ping 3 times at 1s interval (~2s), suppress output, then echo JSON
            return ("bash", "-c \"ping -c 3 -i 1 127.0.0.1 >/dev/null 2>&1; echo '[]'\"");
        }
    }

    /// <summary>
    /// Verifies that two concurrent RunAsync calls are serialized by the process gate.
    /// Each process takes ~2s. Serialized ≈ 4s, parallel ≈ 2s — threshold at 3s.
    /// </summary>
    [Fact]
    public async Task RunAsync_ConcurrentCalls_AreSerializedWhenMaxIs1()
    {
        (string executable, string args) = BuildDelayCommand();

        IOptions<GitHubServiceOptions> options = Options.Create(new GitHubServiceOptions
        {
            GhCli = new GhCliConfiguration
            {
                ExecutablePath = executable,
                MaxConcurrentProcesses = 1,
                ProcessTimeout = "00:00:30",
            },
        });

        GhCliRunner runner = new GhCliRunner(options, NullLogger<GhCliRunner>.Instance);

        Stopwatch sw = Stopwatch.StartNew();

        Task<JsonDocument> t1 = runner.RunAsync(args, CancellationToken.None);
        Task<JsonDocument> t2 = runner.RunAsync(args, CancellationToken.None);

        using JsonDocument d1 = await t1;
        using JsonDocument d2 = await t2;

        sw.Stop();

        // Serialized ≈ 4s, parallel ≈ 2s. Use 3s as dividing line.
        Assert.True(sw.ElapsedMilliseconds >= 3000,
            $"Expected serialized execution (>= 3000ms) but completed in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Verifies that concurrent calls can run in parallel when MaxConcurrentProcesses > 1.
    /// </summary>
    [Fact]
    public async Task RunAsync_ConcurrentCalls_AllowedWhenMaxIsHigher()
    {
        (string executable, string args) = BuildDelayCommand();

        IOptions<GitHubServiceOptions> options = Options.Create(new GitHubServiceOptions
        {
            GhCli = new GhCliConfiguration
            {
                ExecutablePath = executable,
                MaxConcurrentProcesses = 2,
                ProcessTimeout = "00:00:30",
            },
        });

        GhCliRunner runner = new GhCliRunner(options, NullLogger<GhCliRunner>.Instance);

        Stopwatch sw = Stopwatch.StartNew();

        Task<JsonDocument> t1 = runner.RunAsync(args, CancellationToken.None);
        Task<JsonDocument> t2 = runner.RunAsync(args, CancellationToken.None);

        using JsonDocument d1 = await t1;
        using JsonDocument d2 = await t2;

        sw.Stop();

        // Parallel ≈ 2s. Should complete well under 3s.
        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"Expected parallel execution (< 3000ms) but took {sw.ElapsedMilliseconds}ms");
    }
}
