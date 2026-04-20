using System.Text.Json.Nodes;
using FhirAugury.Common.OpenApi;
using FhirAugury.Orchestrator.Configuration;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using NSubstitute;

namespace FhirAugury.Orchestrator.Tests;

/// <summary>
/// CI quality gate. Validates that each service-style OpenAPI document is
/// parseable, has unique operation IDs, and that the orchestrator's merged
/// document only contains paths under <c>/api/v1/source/{name}/</c> for
/// non-orchestrator operations.
///
/// Note: this is a unit-level surrogate for spinning up
/// <c>WebApplicationFactory&lt;Program&gt;</c> for every service. The merge
/// behaviour exercised here mirrors what the orchestrator serves at
/// <c>/api/v1/openapi.json</c>; live documents from each running service are
/// validated structurally by the same merge pipeline.
/// </summary>
public class OpenApiDocumentValidationTests
{
    public static TheoryData<string, string> ServiceStyleDocs()
    {
        TheoryData<string, string> data = [];
        data.Add("orchestrator", BuildDoc("Orchestrator", [
            ("/api/v1/health", "get", "health"),
            ("/api/v1/search", "get", "search"),
            ("/api/v1/source/orchestrator/list-sources", "get", "list-sources"),
        ]));
        data.Add("jira", BuildDoc("Source.Jira", [
            ("/api/v1/query", "post", "query"),
            ("/api/v1/items/{id}", "get", "get-item"),
            ("/api/v1/list-workgroups", "get", "list-workgroups"),
        ]));
        data.Add("zulip", BuildDoc("Source.Zulip", [
            ("/api/v1/query", "post", "query"),
            ("/api/v1/items/{id}", "get", "get-item"),
        ]));
        data.Add("github", BuildDoc("Source.GitHub", [
            ("/api/v1/items/{id}", "get", "get-item"),
            ("/api/v1/search", "get", "search"),
        ]));
        data.Add("confluence", BuildDoc("Source.Confluence", [
            ("/api/v1/items/{id}", "get", "get-item"),
            ("/api/v1/search", "get", "search"),
        ]));
        return data;
    }

    [Theory]
    [MemberData(nameof(ServiceStyleDocs))]
    public void ServiceDocument_ParsesWithoutErrors_AndOperationIdsUnique(string serviceName, string json)
    {
        OpenApiReaderSettings settings = new();
        settings.Readers["json"] = new OpenApiJsonReader();
        ReadResult result = OpenApiDocument.Parse(json, "json", settings);

        Assert.NotNull(result.Document);
        Assert.Empty(result.Diagnostic?.Errors ?? []);

        AssertUniqueOperationIds(json, $"service '{serviceName}'");
    }

    [Fact]
    public async Task OrchestratorMergedDocument_NonOrchestratorPaths_HaveSourcePrefix()
    {
        OpenApiDocument orchestratorDoc = BuildOpenApiDocument([
            ("/api/v1/health", HttpMethod.Get, "health"),
            ("/api/v1/search", HttpMethod.Get, "search"),
            ("/api/v1/source/orchestrator/list-sources", HttpMethod.Get, "list-sources"),
        ]);
        string jiraJson = BuildDoc("Source.Jira", [
            ("/api/v1/query", "post", "query"),
            ("/api/v1/items/{id}", "get", "get-item"),
        ]);
        string zulipJson = BuildDoc("Source.Zulip", [
            ("/api/v1/query", "post", "query"),
        ]);

        OpenApiMergeService service = BuildMergeService(orchestratorDoc, new()
        {
            ["jira"] = jiraJson,
            ["zulip"] = zulipJson,
        });

        MergedDocument merged = await service.GetMergedAsync(includeInternal: false, CancellationToken.None);

        OpenApiReaderSettings settings = new();
        settings.Readers["json"] = new OpenApiJsonReader();
        ReadResult parsed = OpenApiDocument.Parse(merged.Json, "json", settings);
        Assert.NotNull(parsed.Document);
        Assert.Empty(parsed.Diagnostic?.Errors ?? []);

        AssertUniqueOperationIds(merged.Json, "merged orchestrator doc");

        JsonObject root = (JsonObject)JsonNode.Parse(merged.Json)!;
        JsonObject paths = (JsonObject)root["paths"]!;

        HashSet<string> orchestratorOwn = new(StringComparer.Ordinal)
        {
            "/api/v1/health",
            "/api/v1/search",
            "/api/v1/source/orchestrator/list-sources",
        };

        foreach (KeyValuePair<string, JsonNode?> entry in paths)
        {
            if (orchestratorOwn.Contains(entry.Key))
            {
                continue;
            }
            Assert.Matches(@"^/api/v1/source/[a-z][a-z0-9-]*/", entry.Key);
        }

        Assert.True(paths.ContainsKey("/api/v1/source/jira/query"));
        Assert.True(paths.ContainsKey("/api/v1/source/jira/items/{id}"));
        Assert.True(paths.ContainsKey("/api/v1/source/zulip/query"));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static void AssertUniqueOperationIds(string json, string label)
    {
        JsonObject root = (JsonObject)JsonNode.Parse(json)!;
        JsonObject? paths = root["paths"] as JsonObject;
        if (paths is null)
        {
            return;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        HashSet<string> methods = new(StringComparer.OrdinalIgnoreCase)
        {
            "get", "put", "post", "delete", "options", "head", "patch", "trace",
        };

        foreach (KeyValuePair<string, JsonNode?> pathEntry in paths)
        {
            if (pathEntry.Value is not JsonObject pathItem)
            {
                continue;
            }

            foreach (KeyValuePair<string, JsonNode?> methodEntry in pathItem)
            {
                if (!methods.Contains(methodEntry.Key))
                {
                    continue;
                }
                if (methodEntry.Value is not JsonObject op)
                {
                    continue;
                }
                string? opId = op["operationId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(opId))
                {
                    continue;
                }
                Assert.True(seen.Add(opId),
                    $"Duplicate operationId '{opId}' in {label} at {pathEntry.Key} {methodEntry.Key}");
            }
        }
    }

    private static string BuildDoc(string title, (string Path, string Method, string OperationId)[] operations)
    {
        JsonObject paths = [];
        foreach ((string path, string method, string operationId) in operations)
        {
            JsonObject op = new()
            {
                ["operationId"] = operationId,
                ["responses"] = new JsonObject
                {
                    ["200"] = new JsonObject { ["description"] = "OK" },
                },
            };
            if (paths[path] is not JsonObject pathItem)
            {
                pathItem = [];
                paths[path] = pathItem;
            }
            pathItem[method] = op;
        }

        JsonObject doc = new()
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = title,
                ["version"] = "1.0.0",
            },
            ["paths"] = paths,
        };
        return doc.ToJsonString();
    }

    private static OpenApiDocument BuildOpenApiDocument((string Path, HttpMethod Method, string OperationId)[] operations)
    {
        OpenApiDocument doc = new()
        {
            Info = new OpenApiInfo { Title = "Orchestrator", Version = "1.0.0" },
            Paths = [],
            Components = new OpenApiComponents { Schemas = new Dictionary<string, IOpenApiSchema>() },
        };
        foreach ((string path, HttpMethod method, string operationId) in operations)
        {
            OpenApiOperation operation = new()
            {
                OperationId = operationId,
                Responses = new OpenApiResponses
                {
                    ["200"] = new OpenApiResponse { Description = "OK" },
                },
            };
            if (!doc.Paths.TryGetValue(path, out IOpenApiPathItem? existing) || existing is not OpenApiPathItem pathItem)
            {
                pathItem = new OpenApiPathItem();
                doc.Paths[path] = pathItem;
            }
            pathItem.Operations ??= new Dictionary<HttpMethod, OpenApiOperation>();
            pathItem.Operations[method] = operation;
        }
        return doc;
    }

    private sealed class StubDocumentProvider(OpenApiDocument doc) : IOpenApiDocumentProvider
    {
        public Task<OpenApiDocument> GetOpenApiDocumentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(doc);
    }

    private sealed class CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(factory(request));
    }

    private static OpenApiMergeService BuildMergeService(
        OpenApiDocument orchestratorDoc,
        Dictionary<string, string> sourceJsonByName)
    {
        Dictionary<string, SourceServiceConfig> services = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in sourceJsonByName.Keys)
        {
            services[name] = new SourceServiceConfig { Enabled = true, HttpAddress = $"http://{name}:5000" };
        }

        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        foreach (KeyValuePair<string, string> kvp in sourceJsonByName)
        {
            string name = kvp.Key;
            string json = kvp.Value;
            CannedHandler handler = new(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
            factory.CreateClient($"source-{name}").Returns(_ => new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri($"http://{name}:5000"),
            });
        }

        OrchestratorOptions options = new() { Services = services };
        SourceHttpClient sources = new(factory, Options.Create(options), NullLogger<SourceHttpClient>.Instance);
        return new OpenApiMergeService(sources, new StubDocumentProvider(orchestratorDoc), factory, NullLogger<OpenApiMergeService>.Instance);
    }
}
