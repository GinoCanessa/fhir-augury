using FhirAugury.Tools.IngestionToggle;

namespace FhirAugury.Tools.IngestionToggle.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void ParsesMultipleSettingsAndStartupAlias()
    {
        string[] args = ["--ingestion-paused", "true", "--ingest-on-startup-only=false", "--custom-setting", "TRUE"];

        bool parsed = CliOptions.TryParse(args, out IReadOnlyDictionary<string, bool> settings, out string? error);

        Assert.True(parsed, error);
        Assert.Null(error);
        Assert.Equal(3, settings.Count);
        Assert.True(settings["ingestion-paused"]);
        Assert.False(settings["run-ingestion-on-startup-only"]);
        Assert.True(settings["custom-setting"]);
    }

    [Theory]
    [InlineData("--enable", false)]
    [InlineData("--disable", true)]
    public void LegacyAliases_KeepTheirIngestionMeaning(string alias, bool paused)
    {
        bool parsed = CliOptions.TryParse([alias, "--reload-from-cache-on-startup", "false"],
            out IReadOnlyDictionary<string, bool> settings, out string? error);

        Assert.True(parsed, error);
        Assert.Equal(paused, settings["ingestion-paused"]);
        Assert.False(settings["reload-from-cache-on-startup"]);
    }

    public static TheoryData<string[]> InvalidArguments => new()
    {
        Array.Empty<string>(),
        new[] { "--ingestion-paused" },
        new[] { "--ingestion-paused", "yes" },
        new[] { "--ingestion-paused", "1" },
        new[] { "--ingestion-paused", "null" },
        new[] { "--ingestion-paused", "--enabled", "true" },
        new[] { "--enabled=" },
        new[] { "--enabled=true=false" },
        new[] { "--enabled", "true", "extra" },
        new[] { "--enabled", "true", "--enabled", "false" },
        new[] { "--enabled", "true", "--enabled", "true" },
        new[] { "--ingest-on-startup-only", "true", "--run-ingestion-on-startup-only", "false" },
        new[] { "--enable", "--disable" },
        new[] { "--enable", "--ingestion-paused", "false" },
        new[] { "--enable=true" },
        new[] { "--disable", "extra" },
        new[] { "--help", "--disable" },
        new[] { "--enabled", "true", "--help" },
        new[] { "--help=true" },
        new[] { "enabled", "true" },
        new[] { "-enabled", "true" },
        new[] { "--", "true" },
        new[] { "--Enabled", "true" },
        new[] { "--enabled-", "true" },
        new[] { "--bad--name", "true" },
        new[] { "--bad_name", "true" },
        new[] { "--nested:enabled", "true" },
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void RejectsInvalidArgumentsBeforeExecution(string[] args)
    {
        Assert.False(CliOptions.TryParse(args, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
