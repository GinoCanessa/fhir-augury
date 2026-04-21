using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Controllers;
using FhirAugury.Orchestrator.Routing;
using FhirAugury.Orchestrator.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FhirAugury.Orchestrator.Tests;

/// <summary>
/// Phase B B3: Orchestrator-local <c>health</c> and <c>status</c> endpoints.
/// Health is always 200 with no I/O. Status reports 200 when the in-process
/// source registry is hydrated, 503 otherwise. Neither endpoint reaches out
/// to source services — that is the job of <c>api/v1/services</c>.
/// </summary>
public class LifecycleControllerTests
{
    private static SourceHttpClient BuildSourceClient(Dictionary<string, SourceServiceConfig> services)
    {
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        OrchestratorOptions options = new()
        {
            Services = services,
            Search = new SearchOptions(),
        };
        IOptions<OrchestratorOptions> opts = Options.Create(options);
        return new SourceHttpClient(factory, opts, NullLogger<SourceHttpClient>.Instance);
    }

    private static LifecycleController CreateController(Dictionary<string, SourceServiceConfig> services)
        => new(BuildSourceClient(services));

    private static Dictionary<string, SourceServiceConfig> Hydrated() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [SourceSystems.Jira] = new SourceServiceConfig { Enabled = true, HttpAddress = "http://jira:5001" },
            [SourceSystems.Zulip] = new SourceServiceConfig { Enabled = true, HttpAddress = "http://zulip:5002" },
        };

    private static Dictionary<string, SourceServiceConfig> Empty() =>
        new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, SourceServiceConfig> AllDisabled() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [SourceSystems.Jira] = new SourceServiceConfig { Enabled = false, HttpAddress = "http://jira:5001" },
        };

    private static JsonElement ToJson(object? body)
    {
        string json = JsonSerializer.Serialize(body);
        return JsonDocument.Parse(json).RootElement;
    }

    // ── Health ──────────────────────────────────────────────────────────

    [Fact]
    public void Health_IsAlways200_WhenRegistryHydrated()
    {
        LifecycleController controller = CreateController(Hydrated());

        IActionResult result = controller.GetHealth();

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        JsonElement body = ToJson(ok.Value);
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public void Health_IsAlways200_EvenWhenRegistryEmpty()
    {
        LifecycleController controller = CreateController(Empty());

        IActionResult result = controller.GetHealth();

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        JsonElement body = ToJson(ok.Value);
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    // ── Status ──────────────────────────────────────────────────────────

    [Fact]
    public void Status_HydratedRegistry_Returns200WithDetails()
    {
        LifecycleController controller = CreateController(Hydrated());

        IActionResult result = controller.GetStatus();

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        JsonElement body = ToJson(ok.Value);
        Assert.Equal("ok", body.GetProperty("status").GetString());

        JsonElement registry = body.GetProperty("sourceRegistry");
        Assert.True(registry.GetProperty("hydrated").GetBoolean());
        Assert.Equal(2, registry.GetProperty("enabledCount").GetInt32());

        List<string> names = registry.GetProperty("enabledNames")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains(SourceSystems.Jira, names);
        Assert.Contains(SourceSystems.Zulip, names);
    }

    [Fact]
    public void Status_EmptyRegistry_Returns503WithDetails()
    {
        LifecycleController controller = CreateController(Empty());

        IActionResult result = controller.GetStatus();

        ObjectResult obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, obj.StatusCode);

        JsonElement body = ToJson(obj.Value);
        Assert.Equal("not-ready", body.GetProperty("status").GetString());

        JsonElement registry = body.GetProperty("sourceRegistry");
        Assert.False(registry.GetProperty("hydrated").GetBoolean());
        Assert.Equal(0, registry.GetProperty("enabledCount").GetInt32());
    }

    [Fact]
    public void Status_AllSourcesDisabled_Returns503()
    {
        LifecycleController controller = CreateController(AllDisabled());

        IActionResult result = controller.GetStatus();

        ObjectResult obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, obj.StatusCode);
    }
}
