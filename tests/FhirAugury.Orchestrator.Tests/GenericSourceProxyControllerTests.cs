using System.Net;
using System.Text;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Controllers;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FhirAugury.Orchestrator.Tests;

public class GenericSourceProxyControllerTests
{
    // ── Mock handler ────────────────────────────────────────────────────

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private Func<HttpRequestMessage, Task<HttpResponseMessage>>? _responseFactory;
        public List<HttpRequestMessage> SentRequests { get; } = [];

        public void RespondWith(Func<HttpRequestMessage, HttpResponseMessage> factory) =>
            _responseFactory = req => Task.FromResult(factory(req));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Materialize body so we can assert on it later (after the request
            // has been disposed by HttpClient).
            if (request.Content is not null)
            {
                byte[] buffer = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                ByteArrayContent replacement = new(buffer);
                foreach (KeyValuePair<string, IEnumerable<string>> h in request.Content.Headers)
                {
                    replacement.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                request.Content = replacement;
            }

            SentRequests.Add(request);
            if (_responseFactory is not null)
                return await _responseFactory(request);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    // ── Setup helpers ───────────────────────────────────────────────────

    private static Dictionary<string, SourceServiceConfig> EnabledSources() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["jira"] = new SourceServiceConfig { Enabled = true, HttpAddress = "http://jira:5001" },
            ["zulip"] = new SourceServiceConfig { Enabled = false, HttpAddress = "http://zulip:5002" },
        };

    private static (GenericSourceProxyController Controller, MockHttpMessageHandler Handler) CreateController(
        Dictionary<string, SourceServiceConfig>? services = null)
    {
        services ??= EnabledSources();
        MockHttpMessageHandler handler = new();
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        HttpClient httpClient = new(handler)
        {
            BaseAddress = new Uri(services["jira"].HttpAddress),
        };
        factory.CreateClient("source-jira").Returns(httpClient);

        OrchestratorOptions options = new() { Services = services };
        IOptions<OrchestratorOptions> opts = Options.Create(options);
        SourceHttpClient sourceClient = new(factory, opts, NullLogger<SourceHttpClient>.Instance);

        GenericSourceProxyController controller = new(sourceClient, NullLogger<GenericSourceProxyController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (controller, handler);
    }

    // ── Tests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Forward_DisabledSource_Returns404()
    {
        (GenericSourceProxyController controller, _) = CreateController();
        controller.ControllerContext.HttpContext.Request.Method = "GET";

        IActionResult result = await controller.Forward("zulip", "items", CancellationToken.None);

        NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFound.Value);
    }

    [Fact]
    public async Task Forward_UnknownSource_Returns404()
    {
        (GenericSourceProxyController controller, _) = CreateController();
        controller.ControllerContext.HttpContext.Request.Method = "GET";

        IActionResult result = await controller.Forward("does-not-exist", "items", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Forward_OrchestratorName_Returns404()
    {
        (GenericSourceProxyController controller, _) = CreateController();
        controller.ControllerContext.HttpContext.Request.Method = "GET";

        IActionResult result = await controller.Forward("orchestrator", "anything", CancellationToken.None);

        NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFound.Value);
    }

    [Fact]
    public async Task Forward_FiltersRequestHeadersByAllowlist()
    {
        (GenericSourceProxyController controller, MockHttpMessageHandler handler) = CreateController();
        handler.RespondWith(_ => new HttpResponseMessage(HttpStatusCode.OK));

        HttpRequest request = controller.ControllerContext.HttpContext.Request;
        request.Method = "GET";
        request.Path = "/api/v1/source/jira/items/PROJ-1";
        request.QueryString = new QueryString("?expand=true");
        request.Headers["Accept"] = "application/json";
        request.Headers["User-Agent"] = "augury-test/1.0";
        request.Headers["X-Augury-Trace"] = "abc123";
        request.Headers["Authorization"] = "Bearer secret";   // must be stripped
        request.Headers["Cookie"] = "sid=should-not-forward"; // must be stripped
        request.Headers["X-Custom"] = "nope";                 // must be stripped

        IActionResult result = await controller.Forward("jira", "items/PROJ-1", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        HttpRequestMessage sent = Assert.Single(handler.SentRequests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("/api/v1/items/PROJ-1?expand=true", sent.RequestUri!.PathAndQuery);

        Assert.True(sent.Headers.Contains("Accept"));
        Assert.True(sent.Headers.Contains("User-Agent"));
        Assert.True(sent.Headers.Contains("X-Augury-Trace"));
        Assert.False(sent.Headers.Contains("Authorization"));
        Assert.False(sent.Headers.Contains("Cookie"));
        Assert.False(sent.Headers.Contains("X-Custom"));
    }

    [Fact]
    public async Task Forward_BodyMethod_StreamsBodyAndContentTypeHeader()
    {
        (GenericSourceProxyController controller, MockHttpMessageHandler handler) = CreateController();
        handler.RespondWith(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
        });

        byte[] payload = Encoding.UTF8.GetBytes("{\"q\":\"hi\"}");
        HttpRequest request = controller.ControllerContext.HttpContext.Request;
        request.Method = "POST";
        request.Path = "/api/v1/source/jira/query";
        request.ContentType = "application/json";
        request.ContentLength = payload.Length;
        request.Body = new MemoryStream(payload);

        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();

        IActionResult result = await controller.Forward("jira", "query", CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        HttpRequestMessage sent = Assert.Single(handler.SentRequests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.NotNull(sent.Content);
        string sentBody = await sent.Content!.ReadAsStringAsync();
        Assert.Equal("{\"q\":\"hi\"}", sentBody);
        Assert.Equal("application/json", sent.Content.Headers.ContentType?.MediaType);
        Assert.Equal(StatusCodes.Status201Created, controller.ControllerContext.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task Forward_UpstreamUnreachable_Returns502()
    {
        (GenericSourceProxyController controller, MockHttpMessageHandler handler) = CreateController();
        handler.RespondWith(_ => throw new HttpRequestException("connection refused"));

        controller.ControllerContext.HttpContext.Request.Method = "GET";

        IActionResult result = await controller.Forward("jira", "items", CancellationToken.None);

        ObjectResult obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
    }
}
