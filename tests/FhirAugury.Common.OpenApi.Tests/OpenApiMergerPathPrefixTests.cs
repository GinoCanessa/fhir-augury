using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace FhirAugury.Common.OpenApi.Tests;

public class OpenApiMergerPathPrefixTests
{
    [Fact]
    public void Merge_MergedPaths_StartWithSourcePrefix()
    {
        OpenApiDocument orchestrator = new()
        {
            Info = new OpenApiInfo { Title = "Orchestrator", Version = "1.0.0" },
            Paths = [],
            Components = new OpenApiComponents { Schemas = new Dictionary<string, IOpenApiSchema>() },
        };
        AddOp(orchestrator, "/api/v1/health", HttpMethod.Get, "health");
        AddOp(orchestrator, "/api/v1/source/orchestrator/list-sources", HttpMethod.Get, "list-sources");

        OpenApiDocument jira = new()
        {
            Info = new OpenApiInfo { Title = "Jira", Version = "1.0.0" },
            Paths = [],
            Components = new OpenApiComponents { Schemas = new Dictionary<string, IOpenApiSchema>() },
        };
        AddOp(jira, "/api/v1/query", HttpMethod.Post, "query");
        AddOp(jira, "/api/v1/items/{id}", HttpMethod.Get, "get-item");
        AddOp(jira, "/api/v1/list-workgroups", HttpMethod.Get, "list-workgroups");

        OpenApiDocument zulip = new()
        {
            Info = new OpenApiInfo { Title = "Zulip", Version = "1.0.0" },
            Paths = [],
            Components = new OpenApiComponents { Schemas = new Dictionary<string, IOpenApiSchema>() },
        };
        AddOp(zulip, "/api/v1/query", HttpMethod.Post, "query");

        OpenApiDocument merged = OpenApiMerger.Merge(
            orchestrator,
            new Dictionary<string, OpenApiDocument?>
            {
                ["jira"] = jira,
                ["zulip"] = zulip,
            },
            includeInternal: false);

        JsonObject root = SerializeAsJsonObject(merged);
        JsonObject paths = (JsonObject)root["paths"]!;

        HashSet<string> orchestratorOwn = new(StringComparer.Ordinal)
        {
            "/api/v1/health",
            "/api/v1/source/orchestrator/list-sources",
        };

        foreach (KeyValuePair<string, JsonNode?> entry in paths)
        {
            if (orchestratorOwn.Contains(entry.Key))
            {
                continue;
            }
            // Source paths must use the typed-proxy shape `/api/v1/{source}/...`,
            // never the retired generic-proxy `/api/v1/source/{name}/...`.
            Assert.Matches(@"^/api/v1/[a-z][a-z0-9-]*/", entry.Key);
            Assert.DoesNotMatch(@"^/api/v1/source/", entry.Key);
        }

        Assert.Contains("/api/v1/jira/query", paths);
        Assert.Contains("/api/v1/jira/items/{id}", paths);
        Assert.Contains("/api/v1/jira/list-workgroups", paths);
        Assert.Contains("/api/v1/zulip/query", paths);
    }

    private static void AddOp(OpenApiDocument doc, string path, HttpMethod method, string operationId)
    {
        OpenApiOperation operation = new()
        {
            OperationId = operationId,
            Responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse { Description = "OK" },
            },
        };

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
