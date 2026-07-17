using System.Net.Http;

namespace FhirAugury.DevUi.Services.ApiCatalog;

public sealed record ApiInvocationResult(
    string Url,
    HttpMethod Method,
    string? RequestBody,
    int StatusCode,
    string ResponseBody,
    long ElapsedMs)
{
    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
}
