using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FhirAugury.Cli.Models;
using FhirAugury.Cli.OpenApi;
using Microsoft.OpenApi;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class CallHandler
{
    private const int DefaultTimeoutSeconds = 60;

    public static async Task<object> HandleAsync(CallRequest request, string orchestratorAddr, CancellationToken ct)
    {
        OpenApiDocumentCache cache = new(orchestratorAddr);
        OpenApiDocument document = await cache.GetMergedAsync(request.Refresh, ct);

        (string path, HttpMethod method, OpenApiOperation op) = OperationResolver.Resolve(
            document, request.Source, request.Operation);

        CallRequestBuilder.BuiltRequest built = CallRequestBuilder.Build(op, path, method, request.Params);

        string baseAddr = orchestratorAddr.TrimEnd('/');
        Uri uri = new(baseAddr + built.RelativeUrl);

        using HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(request.TimeoutSeconds ?? DefaultTimeoutSeconds),
        };

        using HttpRequestMessage httpRequest = new(built.Method, uri);
        foreach (KeyValuePair<string, string> header in built.Headers)
        {
            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (HasBody(built.Method) && request.Body is { } bodyEl)
        {
            string bodyJson = await ResolveBodyAsync(bodyEl, ct).ConfigureAwait(false);
            httpRequest.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await client.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        bool isJson = contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);

        if (!response.IsSuccessStatusCode)
        {
            string errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(errBody, 512)}",
                inner: null,
                statusCode: response.StatusCode);
        }

        if (isJson)
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            JsonElement payload = doc.RootElement.Clone();

            if (request.Raw)
            {
                return payload;
            }
            return new
            {
                httpStatus = (int)response.StatusCode,
                contentType,
                data = payload,
            };
        }
        else
        {
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (request.Raw)
            {
                return text;
            }
            return new
            {
                httpStatus = (int)response.StatusCode,
                contentType,
                bodyText = text,
            };
        }
    }

    private static bool HasBody(HttpMethod method) =>
        string.Equals(method.Method, "POST", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method.Method, "PUT", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method.Method, "PATCH", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ResolveBodyAsync(JsonElement body, CancellationToken ct)
    {
        if (body.ValueKind == JsonValueKind.String)
        {
            string raw = body.GetString() ?? "";
            if (raw == "@-")
            {
                return await Console.In.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            if (raw.StartsWith('@'))
            {
                string filePath = raw[1..];
                return await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            }
            return raw;
        }
        return JsonSerializer.Serialize(body);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";
}

/// <summary>
/// Pure helper that distributes user-supplied <c>params</c> into path-substitutions,
/// query-string entries, and header entries based on an OpenAPI operation.
/// Extracted for testability.
/// </summary>
internal static class CallRequestBuilder
{
    public sealed record BuiltRequest(
        HttpMethod Method,
        string RelativeUrl,
        IReadOnlyList<KeyValuePair<string, string>> Headers);

    public static BuiltRequest Build(
        OpenApiOperation operation,
        string path,
        HttpMethod method,
        IReadOnlyDictionary<string, string>? @params)
    {
        IReadOnlyDictionary<string, string> p = @params
            ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal);

        Dictionary<string, IOpenApiParameter> byName = new(StringComparer.Ordinal);
        if (operation.Parameters is not null)
        {
            foreach (IOpenApiParameter param in operation.Parameters)
            {
                if (!string.IsNullOrEmpty(param.Name))
                {
                    byName[param.Name] = param;
                }
            }
        }

        // Reject unknown parameters before doing any work.
        foreach (string key in p.Keys)
        {
            if (!byName.ContainsKey(key))
            {
                throw new ArgumentException(
                    $"Parameter '{key}' is not defined on operation '{operation.OperationId ?? path}'.");
            }
        }

        string builtPath = path;
        List<string> queryEntries = [];
        List<KeyValuePair<string, string>> headers = [];

        foreach (KeyValuePair<string, IOpenApiParameter> entry in byName)
        {
            string name = entry.Key;
            IOpenApiParameter param = entry.Value;
            bool hasValue = p.TryGetValue(name, out string? value);

            switch (param.In)
            {
                case ParameterLocation.Path:
                    if (!hasValue || value is null)
                    {
                        throw new ArgumentException(
                            $"Required path parameter '{name}' is missing for operation '{operation.OperationId ?? path}'.");
                    }
                    builtPath = builtPath.Replace("{" + name + "}", Uri.EscapeDataString(value));
                    break;

                case ParameterLocation.Query:
                    if (hasValue && value is not null)
                    {
                        queryEntries.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
                    }
                    else if (param.Required)
                    {
                        throw new ArgumentException(
                            $"Required query parameter '{name}' is missing for operation '{operation.OperationId ?? path}'.");
                    }
                    break;

                case ParameterLocation.Header:
                    if (hasValue && value is not null)
                    {
                        headers.Add(new KeyValuePair<string, string>(name, value));
                    }
                    else if (param.Required)
                    {
                        throw new ArgumentException(
                            $"Required header parameter '{name}' is missing for operation '{operation.OperationId ?? path}'.");
                    }
                    break;

                default:
                    // Cookie/unsupported locations: ignore.
                    break;
            }
        }

        string relativeUrl = queryEntries.Count > 0
            ? builtPath + "?" + string.Join("&", queryEntries)
            : builtPath;

        return new BuiltRequest(method, relativeUrl, headers);
    }
}
