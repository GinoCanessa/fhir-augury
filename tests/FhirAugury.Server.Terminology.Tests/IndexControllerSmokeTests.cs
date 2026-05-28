using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FhirAugury.Server.Terminology.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FhirAugury.Server.Terminology.Tests;

/// <summary>
/// Smoke test: the service boots via <see cref="WebApplicationFactory{Program}"/>
/// with a single fake package and a temp DB; the
/// <c>/api/v1/terminology/index/status</c> endpoint returns the new
/// Phase 2 payload shape (ready flag, per-package rows, lastRefresh).
/// </summary>
public class IndexControllerSmokeTests : IClassFixture<TerminologyTestFactory>
{
    private readonly TerminologyTestFactory _factory;

    public IndexControllerSmokeTests(TerminologyTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Status_ReturnsExpectedShape()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/terminology/index/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.TryGetProperty("ready", out JsonElement _));
        Assert.True(body.TryGetProperty("packages", out JsonElement packages));
        Assert.Equal(JsonValueKind.Array, packages.ValueKind);
        // We override to a single fake package below.
        Assert.Equal(1, packages.GetArrayLength());

        JsonElement first = packages[0];
        Assert.Equal("hl7.terminology.r4", first.GetProperty("packageId").GetString());
        Assert.Equal("R4", first.GetProperty("fhirVersion").GetString());
        Assert.Equal(0, first.GetProperty("artifactCount").GetInt32());

        Assert.True(body.TryGetProperty("lastRefresh", out JsonElement _));
    }
}

/// <summary>
/// Test factory: overrides config so the service uses a temp database
/// path, a temp package cache, and a single fake package — keeping any
/// actual THO download failure contained to the background hosted
/// service (which captures the exception rather than crashing the host).
/// </summary>
public sealed class TerminologyTestFactory : WebApplicationFactory<Program>
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "augury-terminology-tests-" + Guid.NewGuid().ToString("N"));

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Terminology:DatabasePath"] = Path.Combine(_tempRoot, "terminology.sqlite"),
                ["Terminology:CachePath"] = Path.Combine(_tempRoot, "fhir-cache"),
                ["Terminology:Ports:Http"] = "5300",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.PostConfigure<TerminologyServiceOptions>(o =>
            {
                o.Packages = [new PackageOptions
                {
                    PackageId = "hl7.terminology.r4",
                    FhirVersion = "R4",
                    VersionTag = "latest",
                }];
            });

            // Remove the THO-downloading hosted service so tests don't
            // hit the live registry. The status tracker stays registered
            // (no refresh has run, so lastRefresh is null in the response).
            ServiceDescriptor[] hostedToRemove = services
                .Where(sd =>
                    sd.ServiceType == typeof(IHostedService)
                    && sd.ImplementationFactory is not null
                    && sd.ImplementationFactory.Method.ReturnType
                        == typeof(FhirAugury.Server.Terminology.Hosting.TerminologyStartupRebuildService))
                .ToArray();
            foreach (ServiceDescriptor sd in hostedToRemove)
            {
                services.Remove(sd);
            }
        });

        return base.CreateHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
