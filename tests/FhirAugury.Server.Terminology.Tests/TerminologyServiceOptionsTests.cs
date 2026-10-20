using FhirAugury.Server.Terminology.Configuration;
using Microsoft.Extensions.Configuration;

namespace FhirAugury.Server.Terminology.Tests;

/// <summary>
/// Sanity checks for <see cref="TerminologyServiceOptions"/> defaults
/// and validation. The Phase 6 docker/AppHost wiring assumes the
/// service binds to 5300 by default and that the shipped
/// <c>appsettings.json</c> parses + validates cleanly.
/// </summary>
public class TerminologyServiceOptionsTests
{
    [Fact]
    public void Defaults_BindToPort5300()
    {
        TerminologyServiceOptions opts = new();
        Assert.Equal(5300, opts.Ports.Http);
        Assert.NotEmpty(opts.Packages);
        Assert.Equal("none", opts.Embeddings.Provider);
    }

    [Fact]
    public void Defaults_ValidateCleanly()
    {
        TerminologyServiceOptions opts = new();
        string[] errors = opts.Validate().ToArray();
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_RejectsUnknownEmbeddingsProvider()
    {
        TerminologyServiceOptions opts = new();
        opts.Embeddings.Provider = "openai";
        string[] errors = opts.Validate().ToArray();
        Assert.Contains(errors, e => e.Contains("Embeddings.Provider", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShippedAppSettingsParsesAndValidates()
    {
        string appsettings = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "FhirAugury.Server.Terminology", "appsettings.json");
        appsettings = Path.GetFullPath(appsettings);
        Assert.True(File.Exists(appsettings), $"Expected appsettings.json at {appsettings}");

        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile(appsettings, optional: false)
            .Build();

        TerminologyServiceOptions opts = config
            .GetSection(TerminologyServiceOptions.SectionName)
            .Get<TerminologyServiceOptions>() ?? new TerminologyServiceOptions();

        string[] errors = opts.Validate().ToArray();
        Assert.Empty(errors);
    }
}
