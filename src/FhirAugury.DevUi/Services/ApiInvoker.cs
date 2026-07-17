using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FhirAugury.DevUi.Services.ApiCatalog;

namespace FhirAugury.DevUi.Services;

/// <summary>
/// Generic invoker that drives any <see cref="ApiEndpointDescriptor"/> against an
/// arbitrary HTTP base address. <see cref="OrchestratorClient"/> and
/// <see cref="SourceDirectClient"/> are thin wrappers around this.
/// </summary>
public sealed class ApiInvoker(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public async Task<ApiInvocationResult> InvokeAsync(
        string clientName,
        string httpBase,
        ApiEndpointDescriptor descriptor,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken ct = default)
    {
        ApiBuiltRequest req = ApiUrlBuilder.Build(httpBase, descriptor, values);

        HttpClient client = httpClientFactory.CreateClient(clientName);
        using HttpRequestMessage message = new(req.Method, req.Url);
        if (req.JsonBody is not null)
            message.Content = new StringContent(req.JsonBody, Encoding.UTF8, "application/json");

        Stopwatch sw = Stopwatch.StartNew();
        using HttpResponseMessage response = await client.SendAsync(message, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        long elapsed = sw.ElapsedMilliseconds;

        string formatted = IsJson(response.Content.Headers.ContentType) ? PrettyPrint(body) : body;

        return new ApiInvocationResult(
            req.Url, req.Method, req.JsonBody,
            (int)response.StatusCode, formatted, elapsed);
    }

    public static string PrettyPrint(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, PrettyJson);
        }
        catch
        {
            return json;
        }
    }

    private static bool IsJson(MediaTypeHeaderValue? type)
    {
        if (type?.MediaType is null) return false;
        string m = type.MediaType;
        return m.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || m.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }
}
