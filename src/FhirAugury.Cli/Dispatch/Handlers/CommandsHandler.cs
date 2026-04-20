using FhirAugury.Cli.Models;
using FhirAugury.Cli.OpenApi;
using Microsoft.OpenApi;

namespace FhirAugury.Cli.Dispatch.Handlers;

public static class CommandsHandler
{
    public static async Task<object> HandleAsync(CommandsRequest request, string orchestratorAddr, CancellationToken ct)
    {
        OpenApiDocumentCache cache = new(orchestratorAddr);
        OpenApiDocument document = await cache.GetMergedAsync(request.Refresh, ct);

        List<object> commands = [];
        if (document.Paths is null)
        {
            return new { commands };
        }

        foreach (KeyValuePair<string, IOpenApiPathItem> pathKv in document.Paths)
        {
            if (pathKv.Value?.Operations is null)
            {
                continue;
            }

            foreach (KeyValuePair<HttpMethod, OpenApiOperation> opKv in pathKv.Value.Operations)
            {
                OpenApiOperation op = opKv.Value;
                List<string> tags = ExtractTags(op);
                string source = ResolveSource(tags);

                if (!string.IsNullOrEmpty(request.Source)
                    && !string.Equals(source, request.Source, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(request.Tag)
                    && !tags.Any(t =>
                        string.Equals(t, request.Tag, StringComparison.OrdinalIgnoreCase)
                        || t.StartsWith(request.Tag, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                commands.Add(new
                {
                    operationId = op.OperationId,
                    method = opKv.Key.Method,
                    path = pathKv.Key,
                    summary = op.Summary,
                    source,
                    tags,
                });
            }
        }

        return new { commands };
    }

    internal static List<string> ExtractTags(OpenApiOperation op)
    {
        List<string> tags = [];
        if (op.Tags is null)
        {
            return tags;
        }
        foreach (OpenApiTagReference tagRef in op.Tags)
        {
            if (!string.IsNullOrEmpty(tagRef.Name))
            {
                tags.Add(tagRef.Name);
            }
        }
        return tags;
    }

    internal static string ResolveSource(IReadOnlyList<string> tags)
    {
        foreach (string tag in tags)
        {
            if (tag.StartsWith("source:", StringComparison.OrdinalIgnoreCase))
            {
                string remainder = tag.Substring("source:".Length);
                int slash = remainder.IndexOf('/');
                return slash >= 0 ? remainder.Substring(0, slash) : remainder;
            }
        }
        return "orchestrator";
    }
}
