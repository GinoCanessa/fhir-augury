using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace FhirAugury.Common.OpenApi;

/// <summary>
/// Merges per-source OpenAPI documents into an orchestrator OpenAPI document.
/// Operates on JSON projections of the input documents so the algorithm is
/// trivially correct with respect to <c>$ref</c> rewriting and deep cloning.
/// </summary>
public static class OpenApiMerger
{
    private static readonly HashSet<string> s_httpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "put", "post", "delete", "options", "head", "patch", "trace",
    };

    private const string SchemaRefPrefix = "#/components/schemas/";
    private const string ApiV1Prefix = "/api/v1";
    private const string SourceStatusExtension = "x-augury-source-status";
    private const string VisibilityExtension = "x-augury-visibility";

    public static OpenApiDocument Merge(
        OpenApiDocument orchestrator,
        IReadOnlyDictionary<string, OpenApiDocument?> sources,
        bool includeInternal,
        IReadOnlyDictionary<string, string>? unavailable = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(sources);

        JsonObject root = ToJsonObject(orchestrator);
        JsonObject paths = GetOrAddObject(root, "paths");
        JsonObject components = GetOrAddObject(root, "components");
        JsonObject schemas = GetOrAddObject(components, "schemas");

        HashSet<string> seenOperationIds = CollectOperationIds(paths);

        foreach (KeyValuePair<string, OpenApiDocument?> kvp in sources)
        {
            string sourceName = kvp.Key;
            if (kvp.Value is null)
            {
                continue;
            }

            JsonObject src = ToJsonObject(kvp.Value);

            RewriteSchemaRefs(src, sourceName);

            if (src["paths"] is JsonObject srcPaths)
            {
                foreach (KeyValuePair<string, JsonNode?> pathKvp in srcPaths.ToList())
                {
                    string newPath = RemapPath(pathKvp.Key, sourceName);
                    JsonNode? cloned = pathKvp.Value?.DeepClone();
                    if (cloned is not JsonObject pathItem)
                    {
                        continue;
                    }

                    foreach (string method in pathItem.Select(p => p.Key).ToList())
                    {
                        if (!s_httpMethods.Contains(method))
                        {
                            continue;
                        }

                        if (pathItem[method] is not JsonObject op)
                        {
                            continue;
                        }

                        if (!includeInternal
                            && string.Equals(op[VisibilityExtension]?.GetValue<string>(), "internal", StringComparison.Ordinal))
                        {
                            pathItem.Remove(method);
                            continue;
                        }

                        if (op["operationId"] is JsonNode opIdNode && opIdNode.GetValue<string>() is string opId)
                        {
                            string newOpId = opId.StartsWith(sourceName + ".", StringComparison.Ordinal)
                                ? opId
                                : $"{sourceName}.{opId}";
                            op["operationId"] = newOpId;
                            if (!seenOperationIds.Add(newOpId))
                            {
                                throw new InvalidOperationException(
                                    $"OperationId collision while merging source '{sourceName}': '{newOpId}' already exists.");
                            }
                        }

                        if (op["tags"] is JsonArray tags)
                        {
                            if (tags.Count == 0)
                            {
                                tags.Add($"source:{sourceName}");
                            }
                            else
                            {
                                for (int i = 0; i < tags.Count; i++)
                                {
                                    string? existing = tags[i]?.GetValue<string>();
                                    tags[i] = $"source:{sourceName}/{existing}";
                                }
                            }
                        }
                        else
                        {
                            op["tags"] = new JsonArray($"source:{sourceName}");
                        }
                    }

                    bool hasOperation = pathItem.Any(p => s_httpMethods.Contains(p.Key));
                    if (!hasOperation)
                    {
                        continue;
                    }

                    if (paths[newPath] is JsonObject existingPathItem)
                    {
                        // The orchestrator's typed proxy controllers register the
                        // same paths (e.g., `/api/v1/jira/items`) but expose only
                        // minimal metadata (no schemas / response types). The
                        // source's per-method documentation is richer, so let
                        // source operations override the orchestrator's
                        // method-level entries while preserving any
                        // orchestrator-only methods on the same path.
                        foreach (KeyValuePair<string, JsonNode?> methodEntry in pathItem.ToList())
                        {
                            if (!s_httpMethods.Contains(methodEntry.Key))
                            {
                                continue;
                            }
                            existingPathItem[methodEntry.Key] = methodEntry.Value?.DeepClone();
                        }
                        continue;
                    }

                    paths[newPath] = pathItem;
                }
            }

            if (src["components"]?["schemas"] is JsonObject srcSchemas)
            {
                foreach (KeyValuePair<string, JsonNode?> schemaKvp in srcSchemas.ToList())
                {
                    string newKey = $"{sourceName}_{schemaKvp.Key}";
                    if (schemas.ContainsKey(newKey))
                    {
                        throw new InvalidOperationException(
                            $"Schema collision while merging source '{sourceName}': '{newKey}' already exists.");
                    }
                    schemas[newKey] = schemaKvp.Value?.DeepClone();
                }
            }
        }

        if (unavailable is not null && unavailable.Count > 0)
        {
            JsonObject statusNode = root[SourceStatusExtension] as JsonObject ?? [];
            foreach (KeyValuePair<string, string> entry in unavailable)
            {
                statusNode[entry.Key] = entry.Value ?? "unavailable";
            }
            root[SourceStatusExtension] = statusNode;
        }

        return FromJsonObject(root);
    }

    /// <summary>
    /// Remaps a source-relative path (e.g., <c>/api/v1/items/{id}</c>) into
    /// its orchestrator typed-proxy URL (<c>/api/v1/{sourceName}/items/{id}</c>).
    /// The orchestrator no longer hosts the generic <c>/api/v1/source/{name}/...</c>
    /// catch-all proxy; every source endpoint is reached through a typed proxy
    /// controller that mirrors the source path one-to-one under the
    /// <c>/api/v1/{sourceName}</c> prefix.
    /// </summary>
    private static string RemapPath(string original, string sourceName)
    {
        string remainder;
        if (original.StartsWith(ApiV1Prefix, StringComparison.Ordinal))
        {
            remainder = original.Substring(ApiV1Prefix.Length);
        }
        else
        {
            remainder = original.StartsWith("/", StringComparison.Ordinal) ? original : "/" + original;
        }

        if (string.IsNullOrEmpty(remainder))
        {
            return $"/api/v1/{sourceName}";
        }

        if (!remainder.StartsWith("/", StringComparison.Ordinal))
        {
            remainder = "/" + remainder;
        }

        return $"/api/v1/{sourceName}{remainder}";
    }

    private static void RewriteSchemaRefs(JsonNode? node, string sourceName)
    {
        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in obj.ToList())
            {
                if (entry.Key == "$ref"
                    && entry.Value?.GetValue<string>() is string refValue
                    && refValue.StartsWith(SchemaRefPrefix, StringComparison.Ordinal))
                {
                    string schemaName = refValue.Substring(SchemaRefPrefix.Length);
                    obj[entry.Key] = $"{SchemaRefPrefix}{sourceName}_{schemaName}";
                }
                else
                {
                    RewriteSchemaRefs(entry.Value, sourceName);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (JsonNode? item in arr)
            {
                RewriteSchemaRefs(item, sourceName);
            }
        }
    }

    private static HashSet<string> CollectOperationIds(JsonObject paths)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> pathKvp in paths)
        {
            if (pathKvp.Value is not JsonObject pathItem)
            {
                continue;
            }

            foreach (KeyValuePair<string, JsonNode?> methodKvp in pathItem)
            {
                if (!s_httpMethods.Contains(methodKvp.Key))
                {
                    continue;
                }

                if (methodKvp.Value is JsonObject op
                    && op["operationId"]?.GetValue<string>() is string opId
                    && !string.IsNullOrEmpty(opId))
                {
                    ids.Add(opId);
                }
            }
        }
        return ids;
    }

    private static JsonObject GetOrAddObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
        {
            return existing;
        }
        JsonObject created = [];
        parent[key] = created;
        return created;
    }

    private static JsonObject ToJsonObject(OpenApiDocument document)
    {
        using MemoryStream ms = new();
        using (StreamWriter sw = new(ms, leaveOpen: true))
        {
            OpenApiJsonWriter writer = new(sw);
            document.SerializeAsV31(writer);
        }
        ms.Position = 0;
        JsonNode? node = JsonNode.Parse(ms);
        return node as JsonObject
            ?? throw new InvalidOperationException("OpenApiDocument did not serialize to a JSON object.");
    }

    private static OpenApiDocument FromJsonObject(JsonObject root)
    {
        string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        OpenApiReaderSettings settings = new();
        if (!settings.Readers.ContainsKey(OpenApiConstants.Json))
        {
            settings.Readers[OpenApiConstants.Json] = new OpenApiJsonReader();
        }
        ReadResult result = OpenApiDocument.Parse(json, OpenApiConstants.Json, settings);
        return result.Document
            ?? throw new InvalidOperationException("Failed to re-parse merged OpenAPI document.");
    }
}
