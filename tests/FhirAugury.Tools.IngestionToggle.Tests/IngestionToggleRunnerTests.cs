using FhirAugury.Tools.IngestionToggle;

namespace FhirAugury.Tools.IngestionToggle.Tests;

public sealed class IngestionToggleRunnerTests
{
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
        Assert.Contains("No <Section>.IngestionPaused setting found.", output.ToString());
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

    private static string CreateTemporaryRepo()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        return root;
    }
}
