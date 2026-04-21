using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using NSubstitute;

namespace FhirAugury.Orchestrator.Tests;

public class OpenApiMergeServiceTests
{
    private sealed class StubDocumentProvider : IOpenApiDocumentProvider
    {
        private readonly OpenApiDocument _doc;
        public StubDocumentProvider(OpenApiDocument doc) => _doc = doc;
        public Task<OpenApiDocument> GetOpenApiDocumentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_doc);
    }

    private sealed class CannedHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;
        public int CallCount { get; private set; }
        public CannedHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) => _factory = factory;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_factory(request));
        }
    }

    private static OpenApiDocument MakeOrchestratorDoc()
    {
        OpenApiDocument doc = new()
        {
            Info = new OpenApiInfo { Title = "Orchestrator", Version = "1.0.0" },
            Paths = [],
            Components = new OpenApiComponents { Schemas = new Dictionary<string, IOpenApiSchema>() },
        };
        OpenApiOperation healthOp = new()
        {
            OperationId = "health",
            Responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse { Description = "OK" },
            },
        };
        OpenApiPathItem pathItem = new()
        {
            Operations = new Dictionary<HttpMethod, OpenApiOperation>
            {
                [HttpMethod.Get] = healthOp,
            },
        };
        doc.Paths["/api/v1/health"] = pathItem;
        return doc;
    }

    private const string JiraDocJson = """
        {
          "openapi": "3.1.0",
          "info": { "title": "Jira", "version": "1.0.0" },
          "paths": {
            "/api/v1/query": {
              "post": {
                "operationId": "query",
                "responses": { "200": { "description": "OK" } }
              }
            }
          }
        }
        """;

    private static (OpenApiMergeService Service, CannedHttpMessageHandler Handler) BuildService(
        Func<HttpRequestMessage, HttpResponseMessage> jiraResponse)
    {
        OpenApiDocument orchestratorDoc = MakeOrchestratorDoc();
        StubDocumentProvider provider = new(orchestratorDoc);

        Dictionary<string, SourceServiceConfig> services = new(StringComparer.OrdinalIgnoreCase)
        {
            ["jira"] = new SourceServiceConfig { Enabled = true, HttpAddress = "http://jira:5160" },
        };
        OrchestratorOptions options = new() { Services = services };

        CannedHttpMessageHandler handler = new(jiraResponse);
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("source-jira").Returns(_ => new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://jira:5160"),
        });

        SourceHttpClient sources = new(factory, Options.Create(options), NullLogger<SourceHttpClient>.Instance);

        OpenApiMergeService service = new(
            sources,
            provider,
            factory,
            NullLogger<OpenApiMergeService>.Instance);

        return (service, handler);
    }

    [Fact]
    public async Task GetMergedAsync_IncludesOrchestratorAndSourcePaths()
    {
        (OpenApiMergeService service, _) = BuildService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JiraDocJson, Encoding.UTF8, "application/json"),
        });

        MergedDocument merged = await service.GetMergedAsync(includeInternal: false, CancellationToken.None);

        JsonObject root = (JsonObject)JsonNode.Parse(merged.Json)!;
        JsonObject paths = (JsonObject)root["paths"]!;

        Assert.True(paths.ContainsKey("/api/v1/health"));
        Assert.True(paths.ContainsKey("/api/v1/jira/query"));
    }

    [Fact]
    public async Task GetMergedAsync_ReturnsStableETag_OnRepeatedCalls()
    {
        (OpenApiMergeService service, CannedHttpMessageHandler handler) = BuildService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JiraDocJson, Encoding.UTF8, "application/json"),
        });

        MergedDocument first = await service.GetMergedAsync(includeInternal: false, CancellationToken.None);
        MergedDocument second = await service.GetMergedAsync(includeInternal: false, CancellationToken.None);

        Assert.Equal(first.ETag, second.ETag);
        Assert.Equal(first.Json, second.Json);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetMergedAsync_SourceUnavailable_AttachesStatusExtension_AndOmitsSourcePaths()
    {
        (OpenApiMergeService service, _) = BuildService(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        MergedDocument merged = await service.GetMergedAsync(includeInternal: false, CancellationToken.None);

        JsonObject root = (JsonObject)JsonNode.Parse(merged.Json)!;
        JsonObject? status = root["x-augury-source-status"] as JsonObject;
        Assert.NotNull(status);
        Assert.True(status!.ContainsKey("jira"));

        JsonObject paths = (JsonObject)root["paths"]!;
        Assert.False(paths.ContainsKey("/api/v1/jira/query"));
    }

    [Fact]
    public async Task GetMergedAsync_NetworkError_AttachesStatusExtension()
    {
        (OpenApiMergeService service, _) = BuildService(_ => throw new HttpRequestException("connection refused"));

        MergedDocument merged = await service.GetMergedAsync(includeInternal: false, CancellationToken.None);

        JsonObject root = (JsonObject)JsonNode.Parse(merged.Json)!;
        JsonObject? status = root["x-augury-source-status"] as JsonObject;
        Assert.NotNull(status);
        Assert.True(status!.ContainsKey("jira"));
    }
}
