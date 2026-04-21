using System.Net;
using System.Text;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FhirAugury.Orchestrator.Tests.Proxies;

/// <summary>
/// Shared test helpers for typed-proxy controller tests. Captures the upstream
/// HTTP requests sent to source services and the resulting IActionResult so
/// per-source tests can assert URL/headers/body/status round-tripping.
/// </summary>
internal static class ProxyTestSupport
{
    public sealed class CapturingHandler(string responseBody = "{}",
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string contentType = "application/json",
        string? responseEtag = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));

            HttpResponseMessage response = new(statusCode);
            if (statusCode != HttpStatusCode.NotModified)
            {
                response.Content = new StringContent(responseBody, Encoding.UTF8, contentType);
            }
            else
            {
                response.Content = new ByteArrayContent([]);
            }
            if (responseEtag is not null)
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(responseEtag);
            return response;
        }
    }

    public static (SourceHttpClient Client, CapturingHandler Handler) CreateClient(
        string sourceName,
        string responseBody = "{}",
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? responseEtag = null,
        bool enabled = true)
    {
        Dictionary<string, SourceServiceConfig> services = new(StringComparer.OrdinalIgnoreCase)
        {
            [sourceName] = new SourceServiceConfig { Enabled = enabled, HttpAddress = $"http://{sourceName}:5000" },
        };

        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        CapturingHandler handler = new(responseBody, statusCode, responseEtag: responseEtag);
        HttpClient httpClient = new(handler) { BaseAddress = new Uri($"http://{sourceName}:5000") };
        factory.CreateClient($"source-{sourceName.ToLowerInvariant()}").Returns(httpClient);

        OrchestratorOptions options = new() { Services = services };
        SourceHttpClient src = new(factory, Options.Create(options), NullLogger<SourceHttpClient>.Instance);
        return (src, handler);
    }

    public static void SetRequest(ControllerBase controller,
        string method = "GET",
        string queryString = "",
        string? body = null,
        string contentType = "application/json",
        IDictionary<string, string>? headers = null)
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        if (!string.IsNullOrEmpty(queryString))
            context.Request.QueryString = new QueryString(queryString.StartsWith('?') ? queryString : "?" + queryString);
        if (body is not null)
        {
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            context.Request.ContentType = contentType;
            context.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
        }
        if (headers is not null)
        {
            foreach (KeyValuePair<string, string> h in headers)
                context.Request.Headers[h.Key] = h.Value;
        }
        context.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    /// <summary>
    /// Executes an IActionResult against the controller's HttpContext and
    /// returns the captured response body + status code from the response.
    /// </summary>
    public static async Task<(int Status, string Body, string? ETag, string? ContentType)> ExecuteAsync(
        ControllerBase controller, IActionResult result)
    {
        ActionContext ctx = new(
            controller.HttpContext,
            controller.RouteData ?? new Microsoft.AspNetCore.Routing.RouteData(),
            controller.ControllerContext.ActionDescriptor ?? new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor());
        await result.ExecuteResultAsync(ctx);
        controller.HttpContext.Response.Body.Position = 0;
        using StreamReader reader = new(controller.HttpContext.Response.Body);
        string body = await reader.ReadToEndAsync();
        string? etag = controller.HttpContext.Response.Headers.ETag.Count > 0
            ? controller.HttpContext.Response.Headers.ETag[0]
            : null;
        return (controller.HttpContext.Response.StatusCode, body, etag, controller.HttpContext.Response.ContentType);
    }
}
