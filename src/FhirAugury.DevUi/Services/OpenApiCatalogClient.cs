using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace FhirAugury.DevUi.Services;

/// <summary>
/// Fetches and caches per-source OpenAPI documents and exposes a small helper
/// for locating the operation that matches a catalog descriptor's
/// path-template + HTTP method. Used by the API tester page to surface inline
/// schema information (notably the <c>requestBody</c> shape) so users can see
/// what a generic <c>body</c> parameter expects.
/// </summary>
public sealed class OpenApiCatalogClient(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    private readonly ConcurrentDictionary<string, JsonDocument> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the cached OpenAPI document for the given HTTP base address,
    /// fetching it from <c>{httpBase}/api/v1/openapi.json</c> on first use.
    /// </summary>
    public async Task<JsonDocument> GetOrFetchAsync(string clientName, string httpBase, CancellationToken ct = default)
    {
        string key = httpBase.TrimEnd('/');
        if (_cache.TryGetValue(key, out JsonDocument? existing))
        {
            return existing;
        }

        HttpClient client = httpClientFactory.CreateClient(clientName);
        string url = $"{key}/api/v1/openapi.json";
        using HttpResponseMessage response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);

        JsonDocument doc = JsonDocument.Parse(json);
        _cache[key] = doc;
        return doc;
    }

    /// <summary>
    /// Drops cached documents so the next call re-fetches from upstream.
    /// </summary>
    public void Clear() => _cache.Clear();

    /// <summary>
    /// Looks up the operation matching <paramref name="pathTemplate"/> and
    /// <paramref name="method"/> in <paramref name="document"/>. Returns a
    /// summary of the request shape suitable for direct display.
    /// </summary>
    public static OpenApiOperationInfo? FindOperation(JsonDocument document, string pathTemplate, HttpMethod method)
    {
        if (!document.RootElement.TryGetProperty("paths", out JsonElement paths)
            || paths.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Catalog templates are typically "api/v1/..." (no leading slash);
        // OpenAPI paths are "/api/v1/...". Try both forms.
        string normalized = "/" + pathTemplate.TrimStart('/');
        JsonElement pathItem = default;
        bool found = paths.TryGetProperty(normalized, out pathItem)
            || paths.TryGetProperty(pathTemplate, out pathItem);
        if (!found)
        {
            return null;
        }

        string verb = method.Method.ToLowerInvariant();
        if (!pathItem.TryGetProperty(verb, out JsonElement op))
        {
            return null;
        }

        string? operationId = op.TryGetProperty("operationId", out JsonElement opId) && opId.ValueKind == JsonValueKind.String
            ? opId.GetString() : null;
        string? summary = op.TryGetProperty("summary", out JsonElement sum) && sum.ValueKind == JsonValueKind.String
            ? sum.GetString() : null;
        string? description = op.TryGetProperty("description", out JsonElement desc) && desc.ValueKind == JsonValueKind.String
            ? desc.GetString() : null;

        string? requestBody = null;
        if (op.TryGetProperty("requestBody", out JsonElement reqBody))
        {
            requestBody = SerializeWithRefs(reqBody, document);
        }

        string? parameters = null;
        if (op.TryGetProperty("parameters", out JsonElement parms) && parms.ValueKind == JsonValueKind.Array)
        {
            parameters = SerializeWithRefs(parms, document);
        }

        return new OpenApiOperationInfo(operationId, summary, description, requestBody, parameters);
    }

    private static string SerializeWithRefs(JsonElement element, JsonDocument document)
    {
        // Inline a single level of $ref so the displayed schema is useful
        // without requiring the user to chase references manually.
        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms, new JsonWriterOptions { Indented = true }))
        {
            WriteWithRefs(element, writer, document, depth: 0);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteWithRefs(JsonElement element, Utf8JsonWriter writer, JsonDocument document, int depth)
    {
        const int MaxDepth = 4;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (depth <= MaxDepth
                    && element.TryGetProperty("$ref", out JsonElement refEl)
                    && refEl.ValueKind == JsonValueKind.String
                    && TryResolveRef(document, refEl.GetString()!, out JsonElement target))
                {
                    WriteWithRefs(target, writer, document, depth + 1);
                    return;
                }
                writer.WriteStartObject();
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    WriteWithRefs(prop.Value, writer, document, depth + 1);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                    WriteWithRefs(item, writer, document, depth + 1);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool TryResolveRef(JsonDocument document, string reference, out JsonElement target)
    {
        target = default;
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return false;
        string[] segments = reference[2..].Split('/');
        JsonElement current = document.RootElement;
        foreach (string raw in segments)
        {
            string segment = raw.Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return false;
        }
        target = current;
        return true;
    }
}

/// <summary>
/// Compact summary of the request shape for an OpenAPI operation, formatted
/// for direct display in the API tester UI.
/// </summary>
public sealed record OpenApiOperationInfo(
    string? OperationId,
    string? Summary,
    string? Description,
    string? RequestBodyJson,
    string? ParametersJson);
