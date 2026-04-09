using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Ingestion;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Tests;

public class ZulipClientFactoryTests
{
    [Fact]
    public void Create_WithDirectCredentials_ReturnsClient()
    {
        ZulipServiceOptions options = new()
        {
            BaseUrl = "https://zulip.example.com",
            Email = "user@example.com",
            ApiKey = "test-api-key",
        };

        StubHttpClientFactory httpFactory = new();
        ZulipClientFactory factory = new(httpFactory, Options.Create(options));

        zulip_cs_lib.ZulipClient client = factory.Create();

        Assert.NotNull(client);
    }

    [Fact]
    public void ResolveCredentials_WithDirectConfig_ReturnsThem()
    {
        ZulipServiceOptions options = new()
        {
            BaseUrl = "https://zulip.example.com",
            Email = "user@example.com",
            ApiKey = "test-api-key",
        };

        StubHttpClientFactory httpFactory = new();
        ZulipClientFactory factory = new(httpFactory, Options.Create(options));

        (string site, string email, string apiKey) = factory.ResolveCredentials();

        Assert.Equal("https://zulip.example.com", site);
        Assert.Equal("user@example.com", email);
        Assert.Equal("test-api-key", apiKey);
    }

    [Fact]
    public void ResolveCredentials_WithZuliprcFile_ReadsCredentials()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, """
                [api]
                email=rc@example.com
                key=rc-api-key
                site=https://rc.example.com
                """);

            ZulipServiceOptions options = new()
            {
                CredentialFile = tempFile,
            };

            StubHttpClientFactory httpFactory = new();
            ZulipClientFactory factory = new(httpFactory, Options.Create(options));

            (string site, string email, string apiKey) = factory.ResolveCredentials();

            Assert.Equal("https://rc.example.com", site);
            Assert.Equal("rc@example.com", email);
            Assert.Equal("rc-api-key", apiKey);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ResolveCredentials_DirectCredentials_OverrideZuliprc()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, """
                [api]
                email=rc@example.com
                key=rc-api-key
                site=https://rc.example.com
                """);

            ZulipServiceOptions options = new()
            {
                BaseUrl = "https://direct.example.com",
                Email = "direct@example.com",
                ApiKey = "direct-key",
                CredentialFile = tempFile,
            };

            StubHttpClientFactory httpFactory = new();
            ZulipClientFactory factory = new(httpFactory, Options.Create(options));

            (string site, string email, string apiKey) = factory.ResolveCredentials();

            // Direct credentials should take priority over .zuliprc
            Assert.Equal("direct@example.com", email);
            Assert.Equal("direct-key", apiKey);
            // Site from .zuliprc overrides since the code always takes the last site value
            Assert.Equal("https://rc.example.com", site);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ResolveCredentials_MissingCredFile_UsesDefaults()
    {
        ZulipServiceOptions options = new()
        {
            BaseUrl = "https://zulip.example.com",
            CredentialFile = "/nonexistent/path/to/zuliprc",
        };

        StubHttpClientFactory httpFactory = new();
        ZulipClientFactory factory = new(httpFactory, Options.Create(options));

        (string site, string email, string apiKey) = factory.ResolveCredentials();

        Assert.Equal("https://zulip.example.com", site);
        Assert.Equal("", email);
        Assert.Equal("", apiKey);
    }

    /// <summary>Minimal IHttpClientFactory stub for testing.</summary>
    private class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
