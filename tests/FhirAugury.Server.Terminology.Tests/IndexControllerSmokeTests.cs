using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FhirAugury.Server.Terminology.Tests;

/// <summary>
/// Phase 1 smoke test: the service boots via <see cref="WebApplicationFactory{Program}"/>
/// and the stub <c>/api/v1/terminology/index/status</c> endpoint returns the
/// expected "not yet implemented" payload shape.
/// </summary>
public class IndexControllerSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public IndexControllerSmokeTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Status_ReturnsStubShape()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/v1/terminology/index/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.TryGetProperty("ready", out JsonElement ready));
        Assert.False(ready.GetBoolean());

        Assert.True(body.TryGetProperty("message", out JsonElement message));
        Assert.Equal("index not yet implemented", message.GetString());
    }
}
