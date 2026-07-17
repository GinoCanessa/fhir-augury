using System.Text.Json.Nodes;
using FhirAugury.Common.OpenApi;
using Microsoft.OpenApi;

namespace FhirAugury.Common.OpenApi.Tests;

public class OpenApiMergerTests
{
    [Fact]
    public void OpenApiMerger_RemapsSourcePaths()
    {
        OpenApiDocument orchestrator = NewDoc("Orchestrator");
        OpenApiDocument jira = NewDoc("Jira");
        AddOperation(jira, "/api/v1/query", HttpMethod.Post, "query", schemaRef: null);
        AddOperation(jira, "/api/v1/list-workgroups", HttpMethod.Get, "list-workgroups", schemaRef: null);

        OpenApiDocument merged = OpenApiMerger.Merge(
            orchestrator,
            new Dictionary<string, OpenApiDocument?> { ["jira"] = jira },
            includeInternal: false);

        JsonObject root = SerializeAsJsonObject(merged);
        JsonObject paths = (JsonObject)root["paths"]!;
        Assert.True(paths.ContainsKey("/api/v1/jira/query"));
        Assert.True(paths.ContainsKey("/api/v1/jira/list-workgroups"));
        Assert.False(paths.ContainsKey("/api/v1/query"));
        Assert.False(paths.ContainsKey("/api/v1/source/jira/query"));
    }

    [Fact]
    public void OpenApiMerger_PrefixesOperationIds()
    {
        OpenApiDocument orchestrator = NewDoc("Orchestrator");
        OpenApiDocument jira = NewDoc("Jira");
        AddOperation(jira, "/api/v1/query", HttpMethod.Post, "query", schemaRef: null);

        OpenApiDocument merged = OpenApiMerger.Merge(
            orchestrator,
            new Dictionary<string, OpenApiDocument?> { ["jira"] = jira },
            includeInternal: false);

        JsonObject root = SerializeAsJsonObject(merged);
        JsonObject op = (JsonObject)((JsonObject)root["paths"]!["/api/v1/jira/query"]!)["post"]!;
        Assert.Equal("jira.query", op["operationId"]!.GetValue<string>());

        JsonArray tags = (JsonArray)op["tags"]!;
        Assert.Single(tags);
        Assert.StartsWith("source:jira", tags[0]!.GetValue<string>());
    }

    [Fact]
    public void OpenApiMerger_NamespacesSchemas_AndRewritesRefs()
    {
        OpenApiDocument orchestrator = NewDoc("Orchestrator");
        OpenApiDocument jira = NewDoc("Jira");

        OpenApiSchema queryRequest = new()
        {
            Type = JsonSchemaType.Object,
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["text"] = new OpenApiSchema { Type = JsonSchemaType.String },
            },
        };
        jira.Components ??= new OpenApiComponents();
        jira.Components.Schemas = new Dictionary<string, IOpenApiSchema>
        {
            ["QueryRequest"] = queryRequest,
        };

        AddOperation(jira, "/api/v1/query", HttpMethod.Post, "query", schemaRef: "QueryRequest");

        OpenApiDocument merged = OpenApiMerger.Merge(
            orchestrator,
            new Dictionary<string, OpenApiDocument?> { ["jira"] = jira },
            includeInternal: false);

        JsonObject root = SerializeAsJsonObject(merged);
        JsonObject schemas = (JsonObject)root["components"]!["schemas"]!;
        Assert.True(schemas.ContainsKey("jira_QueryRequest"));
        Assert.False(schemas.ContainsKey("QueryRequest"));

        JsonObject op = (JsonObject)((JsonObject)root["paths"]!["/api/v1/jira/query"]!)["post"]!;
        string refValue = op["requestBody"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>();
        Assert.Equal("#/components/schemas/jira_QueryRequest", refValue);
    }

    [Fact]
    public void OpenApiMerger_FiltersInternalOperations_WhenIncludeInternalFalse()
    {
        OpenApiDocument orchestrator = NewDoc("Orchestrator");
        OpenApiDocument jira = NewDoc("Jira");

        AddOperation(jira, "/api/v1/query", HttpMethod.Post, "query", schemaRef: null);
        AddOperation(jira, "/api/v1/secret", HttpMethod.Get, "secret", schemaRef: null, visibility: "internal");

        OpenApiDocument mergedFiltered = OpenApiMerger.Merge(
            orchestrator,
            new Dictionary<string, OpenApiDocument?> { ["jira"] = jira },
            includeInternal: false);

        JsonObject filteredRoot = SerializeAsJsonObject(mergedFiltered);
        JsonObject filteredPaths = (JsonObject)filteredRoot["paths"]!;
        Assert.True(filteredPaths.ContainsKey("/api/v1/jira/query"));
        Assert.False(filteredPaths.ContainsKey("/api/v1/jira/secret"));

        OpenApiDocument mergedAll = OpenApiMerger.Merge(
            orchestrator,
            new Dictionary<string, OpenApiDocument?> { ["jira"] = jira },
            includeInternal: true);
        JsonObject allRoot = SerializeAsJsonObject(mergedAll);
        Assert.True(((JsonObject)allRoot["paths"]!).ContainsKey("/api/v1/jira/secret"));
    }

    [Fact]
    public void OpenApiMerger_AttachesSourceStatus_ForUnavailable()
    {
        OpenApiDocument orchestrator = NewDoc("Orchestrator");

        OpenApiDocument merged = OpenApiMerger.Merge(
            orchestrator,
            new Dictionary<string, OpenApiDocument?>(),
            includeInternal: false,
            unavailable: new Dictionary<string, string>
            {
                ["jira"] = "connection refused",
                ["zulip"] = "timeout",
            });

        JsonObject root = SerializeAsJsonObject(merged);
        JsonObject status = (JsonObject)root["x-augury-source-status"]!;
        Assert.Equal("connection refused", status["jira"]!.GetValue<string>());
        Assert.Equal("timeout", status["zulip"]!.GetValue<string>());

        Assert.False((root["paths"] as JsonObject)?.ContainsKey("/api/v1/jira/query") ?? false);
    }

    [Fact]
    public void OpenApiMerger_MergesPathMethods_WhenOrchestratorAlreadyDeclaresTypedProxyRoute()
    {
        OpenApiDocument orchestrator = NewDoc("Orchestrator");
        // Orchestrator's typed proxy already declares /api/v1/jira/query (POST)
        // with thin metadata.
        AddOperation(orchestrator, "/api/v1/jira/query", HttpMethod.Post, "JiraProxy_Query", schemaRef: null);

        OpenApiDocument jira = NewDoc("Jira");
        AddOperation(jira, "/api/v1/query", HttpMethod.Post, "query", schemaRef: null);
        // Source contributes a second method on the same path.
        AddOperation(jira, "/api/v1/query", HttpMethod.Get, "query-info", schemaRef: null);

        OpenApiDocument merged = OpenApiMerger.Merge(
            orchestrator,
            new Dictionary<string, OpenApiDocument?> { ["jira"] = jira },
            includeInternal: false);

        JsonObject root = SerializeAsJsonObject(merged);
        JsonObject pathItem = (JsonObject)root["paths"]!["/api/v1/jira/query"]!;

        // POST overridden by source (richer), GET added by source.
        JsonObject post = (JsonObject)pathItem["post"]!;
        Assert.Equal("jira.query", post["operationId"]!.GetValue<string>());
        JsonObject get = (JsonObject)pathItem["get"]!;
        Assert.Equal("jira.query-info", get["operationId"]!.GetValue<string>());
    }

    [Fact]
    public void OpenApiMerger_NoLongerThrowsOnPathCollision_MergesInstead()
    {
        OpenApiDocument orchestrator = NewDoc("Orchestrator");
        AddOperation(orchestrator, "/api/v1/jira/query", HttpMethod.Post, "JiraProxy_Query", schemaRef: null);

        OpenApiDocument jira = NewDoc("Jira");
        AddOperation(jira, "/api/v1/query", HttpMethod.Post, "query", schemaRef: null);

        // Should not throw — typed-proxy collisions are merged, not rejected.
        OpenApiDocument merged = OpenApiMerger.Merge(
            orchestrator,
            new Dictionary<string, OpenApiDocument?> { ["jira"] = jira },
            includeInternal: false);

        JsonObject root = SerializeAsJsonObject(merged);
        JsonObject post = (JsonObject)((JsonObject)root["paths"]!["/api/v1/jira/query"]!)["post"]!;
        Assert.Equal("jira.query", post["operationId"]!.GetValue<string>());
    }

    private static OpenApiDocument NewDoc(string title)
    {
        return new OpenApiDocument
        {
            Info = new OpenApiInfo { Title = title, Version = "1.0.0" },
            Paths = [],
            Components = new OpenApiComponents
            {
                Schemas = new Dictionary<string, IOpenApiSchema>(),
            },
        };
    }

    private static void AddOperation(
        OpenApiDocument doc,
        string path,
        HttpMethod method,
        string operationId,
        string? schemaRef,
        string? visibility = null)
    {
        OpenApiOperation operation = new()
        {
            OperationId = operationId,
            Responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse { Description = "OK" },
            },
        };

        if (schemaRef is not null)
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchemaReference(schemaRef, doc),
                    },
                },
            };
        }

        if (visibility is not null)
        {
            operation.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
            operation.Extensions["x-augury-visibility"] = new JsonNodeExtension(JsonValue.Create(visibility)!);
        }

        doc.Paths ??= [];
        if (!doc.Paths.TryGetValue(path, out IOpenApiPathItem? existing) || existing is not OpenApiPathItem pathItem)
        {
            pathItem = new OpenApiPathItem();
            doc.Paths[path] = pathItem;
        }
        pathItem.Operations ??= new Dictionary<HttpMethod, OpenApiOperation>();
        pathItem.Operations[method] = operation;
    }

    private static JsonObject SerializeAsJsonObject(OpenApiDocument document)
    {
        using MemoryStream ms = new();
        using (StreamWriter sw = new(ms, leaveOpen: true))
        {
            OpenApiJsonWriter writer = new(sw);
            document.SerializeAsV31(writer);
        }
        ms.Position = 0;
        return (JsonObject)JsonNode.Parse(ms)!;
    }
}
