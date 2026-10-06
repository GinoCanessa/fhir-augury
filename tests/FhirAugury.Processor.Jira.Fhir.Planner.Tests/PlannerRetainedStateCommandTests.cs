using System.Diagnostics;
using System.Runtime.InteropServices;
using FhirAugury.Processor.Jira.Fhir.Planner.Maintenance;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannerRetainedStateCommandTests
{
    [Fact]
    public async Task Dispatch_OnlyExactFirstTokenSelectsOfflineMode()
    {
        Assert.Null(await PlannerRetainedStateCommand.TryRunAsync([]));
        Assert.Null(await PlannerRetainedStateCommand.TryRunAsync(["--urls", "unused"]));
        Assert.Null(await PlannerRetainedStateCommand.TryRunAsync(["retained-state-extra"]));
        Assert.Null(await PlannerRetainedStateCommand.TryRunAsync(["--other", "retained-state"]));
    }

    [Fact]
    public async Task Capture_ChildProcessUsesOnlyExplicitOwnerSettingsWithoutConstructingHost()
    {
        using PlannerRetainedStateCaptureTests.CaptureFixture fixture = new();
        Dictionary<string, string> before = fixture.OriginalHashes();
        (int code, string output, string error) = await RunChildAsync(fixture, fixture.Arguments());

        Assert.True(code == 0, $"exit {code}\n{output}\n{error}");
        Assert.Contains("capture: complete", output, StringComparison.Ordinal);
        Assert.Empty(error);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "forbidden")));
        Assert.DoesNotContain("Now listening", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", output, StringComparison.Ordinal);
        fixture.AssertOriginals(before);
        using PlannerRetainedVerifiedCapture verified = await PlannerRetainedStateCapture.OpenVerifiedAsync(
            fixture.Settings.Bundle, CaptureFixtureCommit, CaptureFixtureTree);
        Assert.Equal("owner-attested", verified.Manifest.Settings.SettingsAuthority);
        Assert.False(verified.Manifest.Settings.RuntimeConfigurationIndependentlyObserved);
        Assert.False(verified.Manifest.Settings.ActivateRunBackedAuthoring);
        Assert.True(verified.Manifest.Settings.StartProcessingOnStartup);
        Assert.True(verified.Manifest.Settings.ReconcileSnapshotsOnStartup);
        Assert.True(verified.Manifest.Settings.HydrationBackfillOnStartup);
        Assert.Equal(1, verified.Completion.FormatVersion);
        Assert.Equal("complete", verified.Completion.Status);
        Assert.Contains(verified.Completion.Files, file => file.Path == "manifest.json" &&
            file.Sha256 == verified.Completion.ManifestSha256);
        Assert.Contains(verified.Completion.Binaries, file => file.Path.EndsWith(".deps.json", StringComparison.Ordinal));
        Assert.Contains(verified.Completion.Binaries, file => file.Path.EndsWith(".runtimeconfig.json", StringComparison.Ordinal));
        Assert.Contains(verified.Completion.Binaries, file => file.Path.EndsWith("e_sqlite3.dll", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing-subcommand")]
    [InlineData("unknown-subcommand")]
    [InlineData("missing-value")]
    [InlineData("wrong-case-boolean")]
    [InlineData("duplicate-option")]
    [InlineData("unknown-option")]
    [InlineData("missing-attestation")]
    [InlineData("duplicate-attestation")]
    [InlineData("invalid-version")]
    [InlineData("invalid-commit")]
    [InlineData("enabled-empty-backup")]
    public async Task MalformedRetainedState_ChildProcessTerminatesWithoutConfigurationFallthrough(string defect)
    {
        using PlannerRetainedStateCaptureTests.CaptureFixture fixture = new();
        List<string> args = fixture.Arguments().ToList();
        switch (defect)
        {
            case "missing-subcommand": args = ["retained-state"]; break;
            case "unknown-subcommand": args = ["retained-state", "rehearse"]; break;
            case "missing-value": args.Add("--retained-root"); break;
            case "wrong-case-boolean": Replace("--activate-run-backed", "False"); break;
            case "duplicate-option": args.AddRange(["--database", fixture.Database]); break;
            case "unknown-option": args.AddRange(["--config", "unused"]); break;
            case "missing-attestation": args.Remove("--confirm-owner-settings"); break;
            case "duplicate-attestation": args.Add("--confirm-owner-settings"); break;
            case "invalid-version": Replace("--snapshot-schema-version", "0"); break;
            case "invalid-commit": Replace("--candidate-commit", "HEAD"); break;
            case "enabled-empty-backup": Replace("--activate-run-backed", "true"); break;
        }
        (int code, string output, string error) = await RunChildAsync(fixture, args);
        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.Contains("invalid-arguments", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Json", error, StringComparison.Ordinal);
        Assert.DoesNotContain("WebApplication", error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.Database + ".owner.lock"));
        Assert.False(Directory.Exists(fixture.Settings.Bundle));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "forbidden")));

        void Replace(string name, string value) => args[args.IndexOf(name) + 1] = value;
    }

    [Fact]
    public void Parse_RequiresEverySettingAndExactValueSyntax()
    {
        using PlannerRetainedStateCaptureTests.CaptureFixture fixture = new();
        string[] all = fixture.Arguments();
        for (int index = 2; index < all.Length - 1; index += 2)
        {
            List<string> missing = all.ToList();
            missing.RemoveRange(index, 2);
            Assert.Throws<ArgumentException>(() => PlannerRetainedStateCommand.Parse(missing.ToArray()));
        }
        foreach (string version in new[] { "-1", "+1", " 1", "1.0", "2147483648" })
        {
            string[] args = (string[])all.Clone();
            args[Array.IndexOf(args, "--snapshot-schema-version") + 1] = version;
            Assert.Throws<ArgumentException>(() => PlannerRetainedStateCommand.Parse(args));
        }
        foreach (string boolean in new[] { "1", "0", "TRUE", "", " false " })
        {
            string[] args = (string[])all.Clone();
            args[Array.IndexOf(args, "--hydration-backfill-on-startup") + 1] = boolean;
            Assert.Throws<ArgumentException>(() => PlannerRetainedStateCommand.Parse(args));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Help_ChildProcessDoesNotConstructHostOrTouchDatabase(bool captureHelp)
    {
        using PlannerRetainedStateCaptureTests.CaptureFixture fixture = new(createDatabase: false);
        string[] args = captureHelp ? ["retained-state", "capture", "--help"] : ["retained-state", "--help"];
        (int code, string output, string error) = await RunChildAsync(fixture, args);
        Assert.Equal(0, code);
        Assert.Equal(PlannerRetainedStateCommand.Help.Trim(), output.Trim());
        Assert.Empty(error);
        Assert.False(File.Exists(fixture.Database));
        Assert.False(File.Exists(fixture.Database + ".owner.lock"));
    }

    [Fact]
    public async Task MissingDatabase_ChildProcessRefusesBeforeCreatingOwnershipFiles()
    {
        using PlannerRetainedStateCaptureTests.CaptureFixture fixture = new(createDatabase: false);
        (int code, string output, string error) = await RunChildAsync(fixture, fixture.Arguments());
        Assert.Equal(20, code);
        Assert.Empty(output);
        Assert.Contains("database-missing-or-not-regular", error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.Database + ".owner.lock"));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "forbidden")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_ChildProcessRefusesAnotherOwnerOrWriterWithoutHostFallthrough(bool competingOwner)
    {
        using PlannerRetainedStateCaptureTests.CaptureFixture fixture = new();
        using PlannerDatabase owner = new(fixture.Database, NullLogger<PlannerDatabase>.Instance, readOnly: true);
        using FileStream? writer = competingOwner ? null : new(
            fixture.Database, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        if (competingOwner)
        {
            owner.AcquireStartupOwnership();
        }
        (int code, string output, string error) = await RunChildAsync(fixture, fixture.Arguments());
        Assert.Equal(20, code);
        Assert.Empty(output);
        Assert.Contains("owner-or-writer-busy", error, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.BundlePath("capture.complete.json")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "forbidden")));
    }

    private const string CaptureFixtureCommit = PlannerRetainedStateCaptureTests.CaptureFixture.Commit;
    private const string CaptureFixtureTree = PlannerRetainedStateCaptureTests.CaptureFixture.Tree;

    private static async Task<(int Code, string Output, string Error)> RunChildAsync(
        PlannerRetainedStateCaptureTests.CaptureFixture fixture, IEnumerable<string> arguments)
    {
        // Any CreateBuilder/configuration fallthrough fails before a host can listen.
        // These are disposable poison files, never a repository configuration edit.
        File.WriteAllText(Path.Combine(fixture.Root, "appsettings.json"), "{ deliberately invalid JSON");
        File.WriteAllText(Path.Combine(fixture.Root, "appsettings.Poisoned.json"), "{ deliberately invalid JSON");
        string runtime = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        string dotnetRoot = Directory.GetParent(runtime)!.Parent!.Parent!.FullName;
        ProcessStartInfo start = new(Path.Combine(dotnetRoot, "dotnet.exe"))
        {
            WorkingDirectory = fixture.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(PlannerRetainedStateCommand).Assembly.Location);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Clear();
        foreach (string name in new[] { "SystemRoot", "WINDIR" })
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                start.Environment[name] = value;
            }
        }
        start.Environment["TEMP"] = Path.Combine(fixture.Settings.Bundle, "normalization", "temp");
        start.Environment["TMP"] = start.Environment["TEMP"];
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Poisoned";
        start.Environment["ASPNETCORE_URLS"] = "not-a-listening-address";
        start.Environment["FHIR_AUGURY_PROCESSOR_JIRA_FHIR_PLANNER_Processing__DatabasePath"] =
            Path.Combine(fixture.Root, "forbidden", "service.db");
        start.Environment["FHIR_AUGURY_PROCESSOR_JIRA_FHIR_PLANNER_Processing__OrchestratorAddress"] =
            "not-a-network-address";
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Child process did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("The exact child command did not terminate; host fallthrough is not allowed.");
        }
        return (process.ExitCode, await output, await error);
    }
}
