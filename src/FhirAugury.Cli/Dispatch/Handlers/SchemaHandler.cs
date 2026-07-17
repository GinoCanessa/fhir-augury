using System.Text.Json;
using FhirAugury.Cli.Models;
using FhirAugury.Cli.OpenApi;
using Microsoft.OpenApi;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class SchemaHandler
{
    public static async Task<object> HandleAsync(SchemaRequest request, string orchestratorAddr, CancellationToken ct)
    {
        OpenApiDocumentCache cache = new(orchestratorAddr);
        OpenApiDocument document = await cache.GetMergedAsync(request.Refresh, ct);

        (string path, HttpMethod method, OpenApiOperation op) = OperationResolver.Resolve(
            document, request.Source, request.Operation);

        List<object> parameters = [];
        if (op.Parameters is not null)
        {
            foreach (IOpenApiParameter param in op.Parameters)
            {
                parameters.Add(new
                {
                    name = param.Name,
                    @in = param.In?.ToString().ToLowerInvariant(),
                    required = param.Required,
                    schema = SerializeSchema(param.Schema),
                });
            }
        }

        object? requestBody = null;
        if (op.RequestBody is not null && op.RequestBody.Content is not null)
        {
            KeyValuePair<string, OpenApiMediaType>? jsonEntry = FirstJsonContent(op.RequestBody.Content);
            if (jsonEntry is { } entry)
            {
                requestBody = new
                {
                    contentType = entry.Key,
                    schema = SerializeSchema(entry.Value.Schema),
                    required = op.RequestBody.Required,
                };
            }
        }

        object? response = null;
        if (op.Responses is not null)
        {
            foreach (KeyValuePair<string, IOpenApiResponse> respKv in op.Responses)
            {
                if (!IsSuccess(respKv.Key) || respKv.Value?.Content is null)
                {
                    continue;
                }
                KeyValuePair<string, OpenApiMediaType>? jsonEntry = FirstJsonContent(respKv.Value.Content);
                if (jsonEntry is { } entry)
                {
                    response = new
                    {
                        status = respKv.Key,
                        contentType = entry.Key,
                        schema = SerializeSchema(entry.Value.Schema),
                    };
                    break;
                }
            }
        }

        return new
        {
            operationId = op.OperationId,
            method = method.Method,
            path,
            summary = op.Summary,
            parameters,
            requestBody,
            response,
        };
    }

    internal static KeyValuePair<string, OpenApiMediaType>? FirstJsonContent(IDictionary<string, OpenApiMediaType> content)
    {
        foreach (KeyValuePair<string, OpenApiMediaType> entry in content)
        {
            if (entry.Key.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }

    internal static bool IsSuccess(string status) =>
        status.Length == 3 && status[0] == '2';

    internal static JsonElement? SerializeSchema(IOpenApiSchema? schema)
    {
        if (schema is null)
        {
            return null;
        }

        using MemoryStream ms = new();
        using (StreamWriter sw = new(ms, leaveOpen: true))
        {
            Microsoft.OpenApi.OpenApiJsonWriter writer = new(sw);
            schema.SerializeAsV31(writer);
        }
        ms.Position = 0;
        using JsonDocument doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }
}
