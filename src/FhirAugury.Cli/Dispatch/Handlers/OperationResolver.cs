using Microsoft.OpenApi;

namespace FhirAugury.Cli.Dispatch.Handlers;

internal static class OperationResolver
{
    /// <summary>
    /// Resolves an OpenAPI operation by matching `{source}.{command}` first, then
    /// falling back to a bare `{command}` operationId.
    /// </summary>
    public static (string Path, HttpMethod Method, OpenApiOperation Operation) Resolve(
        OpenApiDocument document, string source, string command)
    {
        if (document.Paths is null)
        {
            throw new ArgumentException("Merged OpenAPI document contains no paths.");
        }

        string qualified = string.IsNullOrEmpty(source) ? command : $"{source}.{command}";

        (string Path, HttpMethod Method, OpenApiOperation Op)? qualifiedHit = null;
        (string Path, HttpMethod Method, OpenApiOperation Op)? bareHit = null;

        foreach (KeyValuePair<string, IOpenApiPathItem> pathKv in document.Paths)
        {
            if (pathKv.Value?.Operations is null)
            {
                continue;
            }

            foreach (KeyValuePair<HttpMethod, OpenApiOperation> opKv in pathKv.Value.Operations)
            {
                string? opId = opKv.Value.OperationId;
                if (string.IsNullOrEmpty(opId))
                {
                    continue;
                }

                if (string.Equals(opId, qualified, StringComparison.Ordinal))
                {
                    qualifiedHit = (pathKv.Key, opKv.Key, opKv.Value);
                }
                else if (string.Equals(opId, command, StringComparison.Ordinal))
                {
                    bareHit = (pathKv.Key, opKv.Key, opKv.Value);
                }
            }
        }

        if (qualifiedHit is { } q)
        {
            return q;
        }
        if (bareHit is { } b)
        {
            return b;
        }
        throw new ArgumentException(
            $"No operation found matching operationId '{qualified}' or '{command}'.");
    }
}
