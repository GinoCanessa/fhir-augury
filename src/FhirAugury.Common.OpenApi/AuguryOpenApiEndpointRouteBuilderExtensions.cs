using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace FhirAugury.Common.OpenApi;

public static class AuguryOpenApiEndpointRouteBuilderExtensions
{
    public const string JsonRoute = "/api/v1/openapi.json";
    public const string YamlRoute = "/api/v1/openapi.yaml";

    public static IEndpointRouteBuilder MapAuguryOpenApi(
        this IEndpointRouteBuilder endpoints,
        AuguryOpenApiOptions? options = null)
    {
        endpoints.MapOpenApi(JsonRoute);

        endpoints.MapGet(YamlRoute, async (HttpContext http, IOpenApiDocumentProvider provider, CancellationToken ct) =>
        {
            OpenApiDocument document = await provider.GetOpenApiDocumentAsync(ct).ConfigureAwait(false);

            http.Response.ContentType = "application/yaml; charset=utf-8";

            await using StreamWriter writer = new(http.Response.Body, leaveOpen: true);
            OpenApiYamlWriter yamlWriter = new(writer);
            document.SerializeAsV31(yamlWriter);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        });

        // TODO: map a docs UI (Scalar) when IncludeDocsUi is enabled; deferred
        // to a future deliverable.
        _ = options;

        return endpoints;
    }
}
