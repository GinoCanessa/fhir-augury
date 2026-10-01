using System.Diagnostics;
using System.Text;
using FhirAugury.Tools.IngestionToggle;

namespace FhirAugury.Tools.IngestionToggle.Tests;

public sealed class IngestionToggleRunnerTests : IDisposable
{
    private readonly string _repoRoot = Path.Combine(Path.GetTempPath(), $"ingestion-toggle-tests-{Guid.NewGuid():N}");

    [Fact]
    public void IngestionToggleCommand_EnablesNestedSetting()
    {
        string repoRoot = CreateTemporaryRepo();
        string filePath = Path.Combine(repoRoot, "src", "FhirAugury.Source.Jira", "appsettings.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, """
            {
              "Jira": {
                "IngestionPaused": true,
                "Schedule": "daily"
              }
            }
            """);

        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = IngestionToggleRunner.Run(repoRoot, enableIngestion: true, output, error);

        Assert.Equal(0, exitCode);
        string json = File.ReadAllText(filePath);
        Assert.Contains("\"IngestionPaused\": false", json);
        Assert.Contains("\"Schedule\": \"daily\"", json);
        Assert.Contains("CHANGED", output.ToString());
    }

    [Fact]
    public void IngestionToggleCommand_DisablesNestedSetting()
    {
        string repoRoot = CreateTemporaryRepo();
        string filePath = Path.Combine(repoRoot, "src", "FhirAugury.Source.GitHub", "appsettings.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, """
            {
              "GitHub": {
                "IngestionPaused": false,
                "CachePath": "cache"
              }
            }
            """);

        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = IngestionToggleRunner.Run(repoRoot, enableIngestion: false, output, error);

        Assert.Equal(0, exitCode);
        string json = File.ReadAllText(filePath);
        Assert.Contains("\"IngestionPaused\": true", json);
        Assert.Contains("\"CachePath\": \"cache\"", json);
    }

    [Fact]
    public void IngestionToggleCommand_SkipsMissingProperty()
    {
        string repoRoot = CreateTemporaryRepo();
        string filePath = Path.Combine(repoRoot, "src", "FhirAugury.Source.Fhir", "appsettings.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, """
            {
              "Fhir": {
                "BaseUrl": "https://example.test"
              }
            }
            """);

        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = IngestionToggleRunner.Run(repoRoot, enableIngestion: true, output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("SKIPPED", output.ToString());
        Assert.Contains("No requested setting found under Fhir: --ingestion-paused.", output.ToString());
    }

    [Fact]
    public void IngestionToggleCommand_SkipsMissingFile()
    {
        string repoRoot = CreateTemporaryRepo();
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "FhirAugury.Source.Confluence"));
        File.WriteAllText(Path.Combine(repoRoot, "src", "FhirAugury.Source.Confluence", "appsettings.json"), "{\"Confluence\":{}}");

        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = IngestionToggleRunner.Run(repoRoot, enableIngestion: true, output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("SKIPPED", output.ToString());
        Assert.Contains("File does not exist.", output.ToString());
    }

    [Fact]
    public void IngestionToggleCommand_FailsOnMalformedJson()
    {
        string repoRoot = CreateTemporaryRepo();
        string filePath = Path.Combine(repoRoot, "src", "FhirAugury.Source.Zulip", "appsettings.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, "{ not valid json");

        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = IngestionToggleRunner.Run(repoRoot, enableIngestion: true, output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("FAILED", error.ToString());
        Assert.Contains("Malformed JSON", error.ToString());
    }

    [Fact]
    public void IngestionToggleCommand_PreservesUnrelatedSettings()
    {
        string repoRoot = CreateTemporaryRepo();
        string filePath = Path.Combine(repoRoot, "src", "FhirAugury.Source.Jira", "appsettings.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, """
            {
              "Jira": {
                "IngestionPaused": true,
                "Schedule": "7.00:00:00",
                "CachePath": "C:/tmp/cache"
              },
              "Other": {
                "Enabled": true
              }
            }
            """);

        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = IngestionToggleRunner.Run(repoRoot, enableIngestion: true, output, error);

        Assert.Equal(0, exitCode);
        string json = File.ReadAllText(filePath);
        Assert.Contains("\"Schedule\": \"7.00:00:00\"", json);
        Assert.Contains("\"CachePath\": \"C:/tmp/cache\"", json);
        Assert.Contains("\"Enabled\": true", json);
        Assert.Contains("\"IngestionPaused\": false", json);
    }

    [Theory]
    [InlineData("ingestion-paused", "IngestionPaused")]
    [InlineData("ingest-on-startup-only", "RunIngestionOnStartupOnly")]
    [InlineData("run-ingestion-on-startup-only", "RunIngestionOnStartupOnly")]
    [InlineData("reload-from-cache-on-startup", "ReloadFromCacheOnStartup")]
    [InlineData("rebuild-fts-on-startup", "RebuildFtsOnStartup")]
    [InlineData("custom-boolean-setting", "CustomBooleanSetting")]
    [InlineData("custom-boolean-setting", "customBooleanSetting")]
    public void ExplicitFlags_SetAnyExistingBooleanAcrossSources(string flag, string property)
    {
        string[] sources = ["Jira", "Zulip", "GitHub", "Confluence"];
        foreach (string source in sources)
        {
            WriteSettings($"FhirAugury.Source.{source}", $$$"""{"{{{source}}}":{"{{{property}}}":false,"Other":false}}""");
        }

        (int exitCode, string output, string error) = RunWithArguments($"--{flag}", "true");

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Contains("Summary: 4 changed, 0 skipped, 0 failed", output);
        foreach (string source in sources)
        {
            string file = SettingsPath($"FhirAugury.Source.{source}");
            Assert.Equal($$$"""{"{{{source}}}":{"{{{property}}}":true,"Other":false}}""", File.ReadAllText(file));
        }
    }

    [Fact]
    public void MultipleSettings_PreserveEveryOtherByte()
    {
        string original = "{\r\n" +
            "  // Preserve comments, unicode: \u00e9 \u6f22\u5b57\r\n" +
            "  \"Jira\": {\r\n" +
            "    \"Ingestion\\u0050aused\" : false,\r\n" +
            "    \"RunIngestionOnStartupOnly\": true,\r\n" +
            "    \"ReloadFromCacheOnStartup\": false,\r\n" +
            "    \"Nested\": { \"IngestionPaused\": false },\r\n" +
            "    \"Text\": \"IngestionPaused: false\",\r\n" +
            "  },\r\n" +
            "  \"Other\": { \"IngestionPaused\": false },\r\n" +
            "  \"IngestionPaused\": false,\r\n" +
            "}\r\n";
        byte[] before = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(original)];
        string filePath = WriteSettings("FhirAugury.Source.Jira", "");
        File.WriteAllBytes(filePath, before);

        (int exitCode, string output, string error) = RunWithArguments(
            "--ingestion-paused", "true",
            "--ingest-on-startup-only=false",
            "--reload-from-cache-on-startup", "false",
            "--missing-setting", "true");

        string expected = original
            .Replace("\"Ingestion\\u0050aused\" : false", "\"Ingestion\\u0050aused\" : true", StringComparison.Ordinal)
            .Replace("\"RunIngestionOnStartupOnly\": true", "\"RunIngestionOnStartupOnly\": false", StringComparison.Ordinal);
        byte[] expectedBytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(expected)];
        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Equal(expectedBytes, File.ReadAllBytes(filePath));
        Assert.Contains("Jira.IngestionPaused=true", output);
        Assert.Contains("Jira.RunIngestionOnStartupOnly=false", output);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void Discovery_OnlyChangesTheMatchingSourceSectionInSourceLocalFiles()
    {
        const string original = """{"Jira":{"Enabled":false},"Other":{"Enabled":false},"Enabled":false}""";
        string sourceFile = WriteSettings("FhirAugury.Source.Jira", original);
        string[] untouched =
        [
            WriteSettings("FhirAugury.Orchestrator", original),
            WriteSettings("FhirAugury.Processor.Jira.Fhir.Preparer", original),
            WriteSettings("FhirAugury.Source.GitHub", original),
            WriteSettings(Path.Combine("nested", "FhirAugury.Source.Jira"), original),
            WriteSettings("FhirAugury.Source.Jira", original, "appsettings.json"),
            WriteSettings("FhirAugury.Source.Jira", original, "appsettings.Development.json"),
        ];

        (int exitCode, string output, string error) = RunWithArguments("--enabled", "true");

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Equal("""{"Jira":{"Enabled":true},"Other":{"Enabled":false},"Enabled":false}""", File.ReadAllText(sourceFile));
        Assert.All(untouched, path => Assert.Equal(original, File.ReadAllText(path)));
        Assert.DoesNotContain("Orchestrator", output);
        Assert.DoesNotContain("Preparer", output);
        Assert.Contains("Summary: 1 changed, 1 skipped, 0 failed", output);
    }

    [Fact]
    public void MissingAndUnselectedSettings_AreNeverCreatedOrChanged()
    {
        const string original = """{"Jira":{"Nested":{"Enabled":false},"Items":[{"Enabled":false}]},"Enabled":false}""";
        string sourceFile = WriteSettings("FhirAugury.Source.Jira", original);
        Directory.CreateDirectory(Path.Combine(CreateTemporaryRepo(), "src", "FhirAugury.Source.GitHub"));

        (int exitCode, string output, string error) = RunWithArguments("--enabled", "true");

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Equal(original, File.ReadAllText(sourceFile));
        Assert.False(File.Exists(SettingsPath("FhirAugury.Source.GitHub")));
        Assert.Contains("No requested setting found under Jira: --enabled.", output);
        Assert.Contains("Summary: 0 changed, 2 skipped, 0 failed", output);
    }

    [Fact]
    public void AlreadyCorrectSettings_DoNotRewriteTheFile()
    {
        const string original = """{"jira":{"Enabled":true,"RunIngestionOnStartupOnly":false}}""";
        string path = WriteSettings("FhirAugury.Source.Jira", original);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        DateTime lastWrite = File.GetLastWriteTimeUtc(path);

        (int exitCode, string output, string error) = RunWithArguments("--enabled", "true", "--ingest-on-startup-only", "false");

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(path));
        Assert.Contains("Already set: jira.Enabled=true, jira.RunIngestionOnStartupOnly=false.", output);
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("""{"Jira":{"Enabled":false,"Other":null}}""", "not a boolean")]
    [InlineData("""{"Jira":{"Enabled":false,"Other":"true"}}""", "not a boolean")]
    [InlineData("""{"Jira":{"Enabled":false,"Other":1}}""", "not a boolean")]
    [InlineData("""{"Jira":{"Enabled":false,"Other":{}}}""", "not a boolean")]
    [InlineData("""{"Jira":{"Enabled":false,"Other":[]}}""", "not a boolean")]
    [InlineData("""{"Jira":{"Enabled":false,"Enabled":true}}""", "Duplicate setting")]
    [InlineData("""{"Jira":{"Enabled":false,"enabled":true}}""", "Duplicate setting")]
    [InlineData("""{"Jira":{"Enabled":false},"jira":{"Enabled":false}}""", "Duplicate source section")]
    [InlineData("""{"Jira":[]}""", "not an object")]
    [InlineData("""{"Jira":null}""", "not an object")]
    [InlineData("""[{"Enabled":false}]""", "root is not an object")]
    [InlineData("""{"Jira":{"Enabled":false}} trailing""", "Malformed JSON")]
    [InlineData("""{"Jira":{"Enabled":false},"Invalid":}""", "Malformed JSON")]
    public void InvalidFile_RemainsUnchangedWhileOtherSourcesAreProcessed(string original, string failure)
    {
        string invalidFile = WriteSettings("FhirAugury.Source.Jira", original);
        string validFile = WriteSettings("FhirAugury.Source.Zulip", """{"Zulip":{"Enabled":false}}""");

        (int exitCode, string output, string error) = RunWithArguments("--enabled", "true", "--other", "true");

        Assert.Equal(1, exitCode);
        Assert.Contains(failure, error);
        Assert.Equal(original, File.ReadAllText(invalidFile));
        Assert.Equal("""{"Zulip":{"Enabled":true}}""", File.ReadAllText(validFile));
        Assert.Contains("Summary: 1 changed, 0 skipped, 1 failed", output);
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void ReadOnlyFile_IsReportedWithoutChangingItsContents()
    {
        const string original = """{"Jira":{"Enabled":false}}""";
        string path = WriteSettings("FhirAugury.Source.Jira", original);
        FileAttributes attributes = File.GetAttributes(path);
        try
        {
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);

            (int exitCode, string output, string error) = RunWithArguments("--enabled", "true");

            Assert.Equal(1, exitCode);
            Assert.Contains("read-only", error);
            Assert.Contains("Summary: 0 changed, 0 skipped, 1 failed", output);
            Assert.Equal(original, File.ReadAllText(path));
            AssertNoTemporaryFiles();
        }
        finally
        {
            File.SetAttributes(path, attributes);
        }
    }

    [Fact]
    public void ReplacementFailure_PreservesOriginalAndRemovesTemporaryFile()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string original = """{"Jira":{"Enabled":false}}""";
        string path = WriteSettings("FhirAugury.Source.Jira", original);
        using FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        (int exitCode, string output, string error) = RunWithArguments("--enabled", "true");

        Assert.Equal(1, exitCode);
        Assert.Contains("FAILED", error);
        Assert.Contains("Summary: 0 changed, 0 skipped, 1 failed", output);
        Assert.Equal(original, File.ReadAllText(path));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public void Replacement_PreservesUnixPermissions()
    {
        if (OperatingSystem.IsWindows()) return;

        string path = WriteSettings("FhirAugury.Source.Jira", """{"Jira":{"Enabled":false}}""");
        UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(path, mode);

        (int exitCode, _, string error) = RunWithArguments("--enabled", "true");

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Equal(mode, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task CommandLine_UpdatesExplicitSettingsFromARepositorySubdirectory()
    {
        string path = WriteSettings("FhirAugury.Source.Jira",
            """{"Jira":{"IngestionPaused":true,"RunIngestionOnStartupOnly":false,"Other":true}}""");

        (int exitCode, string output, string error) = await RunCommandLineAsync(
            "--ingestion-paused", "false", "--ingest-on-startup-only", "true");

        Assert.Equal(0, exitCode);
        Assert.Empty(error);
        Assert.Equal("""{"Jira":{"IngestionPaused":false,"RunIngestionOnStartupOnly":true,"Other":true}}""",
            File.ReadAllText(path));
        Assert.Contains("Summary: 1 changed, 0 skipped, 0 failed", output);
        AssertNoTemporaryFiles();
    }

    public static TheoryData<string[]> InvalidCommandLineArguments => new()
    {
        new[] { "--ingestion-paused", "true", "--ingest-on-startup-only", "invalid" },
        new[] { "--help", "--disable" },
        new[] { "--ingestion-paused", "true", "unexpected" },
    };

    [Theory]
    [MemberData(nameof(InvalidCommandLineArguments))]
    public async Task CommandLine_InvalidArgumentsLeaveConfigurationUntouched(string[] args)
    {
        const string original = """{"Jira":{"IngestionPaused":false}}""";
        string path = WriteSettings("FhirAugury.Source.Jira", original);

        (int exitCode, string output, string error) = await RunCommandLineAsync(args);

        Assert.Equal(2, exitCode);
        Assert.Empty(output);
        Assert.Contains("Usage:", error);
        Assert.Equal(original, File.ReadAllText(path));
        AssertNoTemporaryFiles();
    }

    private async Task<(int ExitCode, string Output, string Error)> RunCommandLineAsync(params string[] args)
    {
        string root = CreateTemporaryRepo();
        File.WriteAllText(Path.Combine(root, "fhir-augury.slnx"), "<Solution />");
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = Path.Combine(root, "src"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (string argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the configuration tool.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }

        return (process.ExitCode, await output, await error);
    }

    private (int ExitCode, string Output, string Error) RunWithArguments(params string[] args)
    {
        Assert.True(CliOptions.TryParse(args, out IReadOnlyDictionary<string, bool> settings, out string? parseError), parseError);
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = IngestionToggleRunner.Run(CreateTemporaryRepo(), settings, output, error);
        return (exitCode, output.ToString(), error.ToString());
    }

    private string WriteSettings(string project, string contents, string fileName = "appsettings.local.json")
    {
        string directory = Path.Combine(CreateTemporaryRepo(), "src", project);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    private string SettingsPath(string project) =>
        Path.Combine(_repoRoot, "src", project, "appsettings.local.json");

    private void AssertNoTemporaryFiles() =>
        Assert.Empty(Directory.EnumerateFiles(_repoRoot, "*.tmp", SearchOption.AllDirectories));

    private string CreateTemporaryRepo()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "src"));
        return _repoRoot;
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoRoot))
        {
            Directory.Delete(_repoRoot, recursive: true);
        }
    }
}
