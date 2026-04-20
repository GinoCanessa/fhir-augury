using System.Reflection;
using FhirAugury.Common.OpenApi.Attributes;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FhirAugury.Common.OpenApi;

/// <summary>
/// Applies FHIR Augury conventions to the OpenAPI document: deterministic
/// <c>operationId</c>s and vendor extensions harvested from Augury attributes.
/// </summary>
internal sealed class AuguryOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    private readonly AuguryOpenApiOptions _options;

    public AuguryOpenApiDocumentTransformer(AuguryOpenApiOptions options)
    {
        _options = options;
    }

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        Dictionary<(string Method, string Path), MethodInfo> methodLookup = BuildMethodLookup(context);

        if (document.Paths is null)
        {
            return Task.CompletedTask;
        }

        foreach (KeyValuePair<string, IOpenApiPathItem> pathEntry in document.Paths)
        {
            string pathKey = pathEntry.Key;
            if (pathEntry.Value is not OpenApiPathItem pathItem)
            {
                continue;
            }

            if (pathItem.Operations is null)
            {
                continue;
            }

            foreach (KeyValuePair<HttpMethod, OpenApiOperation> opEntry in pathItem.Operations)
            {
                OpenApiOperation operation = opEntry.Value;
                string methodName = opEntry.Key.Method;

                methodLookup.TryGetValue((methodName.ToUpperInvariant(), pathKey), out MethodInfo? methodInfo);

                string controllerName = methodInfo?.DeclaringType?.Name is string dn
                    ? StripControllerSuffix(dn)
                    : string.Empty;
                string actionName = methodInfo?.Name ?? string.Empty;

                AuguryCommandAttribute? commandAttr = methodInfo?.GetCustomAttribute<AuguryCommandAttribute>(inherit: false);
                if (commandAttr is not null)
                {
                    operation.OperationId = string.IsNullOrEmpty(controllerName)
                        ? commandAttr.Name
                        : $"{controllerName}.{commandAttr.Name}";
                    AddExtension(operation, "x-augury-command", commandAttr.Name);
                }
                else if (string.IsNullOrEmpty(operation.OperationId))
                {
                    if (!string.IsNullOrEmpty(controllerName) && !string.IsNullOrEmpty(actionName))
                    {
                        operation.OperationId = $"{Kebab(controllerName)}.{Kebab(actionName)}";
                    }
                }

                if (methodInfo is null)
                {
                    continue;
                }

                if (methodInfo.GetCustomAttribute<AuguryStreamingAttribute>(inherit: false) is not null)
                {
                    AddExtension(operation, "x-augury-streaming", true);
                }

                AuguryVisibilityAttribute? visAttr =
                    methodInfo.GetCustomAttribute<AuguryVisibilityAttribute>(inherit: false)
                    ?? methodInfo.DeclaringType?.GetCustomAttribute<AuguryVisibilityAttribute>(inherit: false);
                if (visAttr is not null)
                {
                    string visValue = visAttr.Visibility == AuguryVisibility.Internal ? "internal" : "public";
                    AddExtension(operation, "x-augury-visibility", visValue);
                }

                if (methodInfo.GetCustomAttribute<AugurySinceAttribute>(inherit: false) is { } sinceAttr)
                {
                    AddExtension(operation, "x-augury-since", sinceAttr.Version);
                }

                if (methodInfo.GetCustomAttribute<AuguryUntilAttribute>(inherit: false) is { } untilAttr)
                {
                    AddExtension(operation, "x-augury-until", untilAttr.Version);
                }
            }
        }

        return Task.CompletedTask;
    }

    private static Dictionary<(string Method, string Path), MethodInfo> BuildMethodLookup(
        OpenApiDocumentTransformerContext context)
    {
        Dictionary<(string Method, string Path), MethodInfo> lookup = [];
        if (context.DescriptionGroups is null)
        {
            return lookup;
        }

        foreach (ApiDescriptionGroup group in context.DescriptionGroups)
        {
            foreach (ApiDescription apiDescription in group.Items)
            {
                if (apiDescription.ActionDescriptor is ControllerActionDescriptor cad
                    && apiDescription.HttpMethod is string verb
                    && apiDescription.RelativePath is string relative)
                {
                    string normalized = "/" + relative.TrimStart('/');
                    lookup[(verb.ToUpperInvariant(), normalized)] = cad.MethodInfo;
                }
            }
        }

        return lookup;
    }

    private static void AddExtension(OpenApiOperation operation, string key, object value)
    {
        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);

        System.Text.Json.Nodes.JsonNode node = value switch
        {
            bool b => System.Text.Json.Nodes.JsonValue.Create(b),
            string s => System.Text.Json.Nodes.JsonValue.Create(s)!,
            _ => System.Text.Json.Nodes.JsonValue.Create(value?.ToString() ?? string.Empty)!,
        };

        operation.Extensions[key] = new JsonNodeExtension(node);
    }

    private static string StripControllerSuffix(string typeName)
    {
        const string suffix = "Controller";
        return typeName.EndsWith(suffix, StringComparison.Ordinal)
            ? typeName[..^suffix.Length]
            : typeName;
    }

    internal static string Kebab(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        System.Text.StringBuilder sb = new(value.Length + 8);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(value[i - 1]) || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                {
                    sb.Append('-');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Trim('-');
    }
}
