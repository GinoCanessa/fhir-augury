using System.Reflection;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.OpenApi;
using FhirAugury.DevUi.Services;
using FhirAugury.DevUi.Services.ApiCatalog;
using FhirAugury.DevUi.Services.ApiCatalog.Catalogs;
using FhirAugury.Orchestrator.Controllers.Proxies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.OpenApi;

namespace FhirAugury.DevUi.Tests;

public class OrchestratorSourceProxyParityTests
{
    public static IEnumerable<object[]> Sources() =>
    [
        [
            "jira",
            typeof(FhirAugury.Source.Jira.Controllers.ItemsController).Assembly,
            (Func<IReadOnlyList<ApiEndpointDescriptor>>)JiraCatalog.Build,
            typeof(JiraProxyController),
        ],
        [
            "zulip",
            typeof(FhirAugury.Source.Zulip.Controllers.ItemsController).Assembly,
            (Func<IReadOnlyList<ApiEndpointDescriptor>>)ZulipCatalog.Build,
            typeof(ZulipProxyController),
        ],
        [
            "confluence",
            typeof(FhirAugury.Source.Confluence.Controllers.ItemsController).Assembly,
            (Func<IReadOnlyList<ApiEndpointDescriptor>>)ConfluenceCatalog.Build,
            typeof(ConfluenceProxyController),
        ],
        [
            "github",
            typeof(FhirAugury.Source.GitHub.Controllers.ItemsController).Assembly,
            (Func<IReadOnlyList<ApiEndpointDescriptor>>)GitHubCatalog.Build,
            typeof(GitHubProxyController),
        ],
        [
            "fhir",
            typeof(FhirAugury.Source.Fhir.Controllers.ReleasesController).Assembly,
            (Func<IReadOnlyList<ApiEndpointDescriptor>>)FhirCatalog.Build,
            typeof(FhirProxyController),
        ],
    ];

    [Theory]
    [MemberData(nameof(Sources))]
    public void Every_source_controller_operation_has_exactly_one_catalog_and_matrix_entry(
        string sourceName,
        Assembly sourceAssembly,
        Func<IReadOnlyList<ApiEndpointDescriptor>> buildCatalog,
        Type proxyController)
    {
        _ = proxyController;
        IReadOnlyList<ApiEndpointDescriptor> catalog = buildCatalog();
        IReadOnlyList<OrchestratorProxyRoute> matrix = OrchestratorProxyRouteMatrix.GetRoutes(sourceName);
        List<(HttpMethod Method, string Route)> sourceRoutes =
            CatalogCoverageTests.EnumerateControllerRoutes(sourceAssembly).ToList();

        List<string> failures = [];
        foreach ((HttpMethod method, string path) in sourceRoutes)
        {
            string routeKey = Key(method, path);
            int catalogMatches = catalog.Count(descriptor =>
                Key(descriptor.Method, descriptor.PathTemplate) == routeKey);
            int matrixMatches = matrix.Count(route =>
                Key(route.SourceDescriptor.Method, route.SourceDescriptor.PathTemplate) == routeKey);

            if (catalogMatches != 1 || matrixMatches != 1)
            {
                failures.Add(
                    $"{routeKey}: catalog entries={catalogMatches}, matrix entries={matrixMatches}");
            }
        }

        foreach (ApiEndpointDescriptor descriptor in catalog)
        {
            string routeKey = Key(descriptor.Method, descriptor.PathTemplate);
            int sourceMatches = sourceRoutes.Count(route =>
                Key(route.Method, route.Route) == routeKey);
            if (sourceMatches != 1)
            {
                failures.Add(
                    $"{routeKey}: source controller entries={sourceMatches} for catalog id '{descriptor.Id}'");
            }
        }

        Assert.True(failures.Count == 0,
            $"{sourceName} source operations without exactly one catalog/matrix disposition:\n  - " +
            string.Join("\n  - ", failures));
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void Typed_proxy_controller_route_set_matches_matrix(
        string sourceName,
        Assembly sourceAssembly,
        Func<IReadOnlyList<ApiEndpointDescriptor>> buildCatalog,
        Type proxyController)
    {
        _ = sourceAssembly;
        _ = buildCatalog;

        HashSet<string> expected = OrchestratorProxyRouteMatrix.GetRoutes(sourceName)
            .Where(route => route.Disposition == OrchestratorProxyRouteDisposition.TypedSourceProxy)
            .Select(route => Key(
                route.SourceDescriptor.Method,
                PrefixSourcePath(sourceName, route.SourceDescriptor.PathTemplate)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // FHIR lifecycle proxies predate the gateway replacement policy and
        // remain as a supported legacy route shape.
        if (string.Equals(sourceName, "fhir", StringComparison.OrdinalIgnoreCase))
        {
            foreach (OrchestratorProxyRoute route in OrchestratorProxyRouteMatrix.GetRoutes(sourceName)
                .Where(route => route.Disposition ==
                    OrchestratorProxyRouteDisposition.OrchestratorReadinessOrMetaReplacement))
            {
                expected.Add(Key(
                    route.SourceDescriptor.Method,
                    PrefixSourcePath(sourceName, route.SourceDescriptor.PathTemplate)));
            }
        }

        HashSet<string> actual = EnumerateControllerRoutes(proxyController)
            .Select(route => Key(route.Method, route.Route))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        List<string> unexpected = actual.Except(expected, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(missing.Count == 0 && unexpected.Count == 0,
            $"{sourceName} proxy route mismatch." +
            $"\nMissing:\n  - {string.Join("\n  - ", missing)}" +
            $"\nUnexpected:\n  - {string.Join("\n  - ", unexpected)}");
    }

    [Fact]
    public void Native_and_replacement_matrix_routes_exist_on_orchestrator_controllers()
    {
        Assembly assembly = typeof(FhirAugury.Orchestrator.Controllers.ContentController).Assembly;
        HashSet<string> actual = assembly.GetTypes()
            .Where(type =>
                typeof(ControllerBase).IsAssignableFrom(type)
                && type.Namespace != typeof(JiraProxyController).Namespace)
            .SelectMany(EnumerateControllerRoutes)
            .Select(route => Key(route.Method, route.Route))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> missing = [];
        foreach (OrchestratorProxyRoute route in OrchestratorProxyRouteMatrix.Routes
            .Where(route => route.Disposition != OrchestratorProxyRouteDisposition.TypedSourceProxy))
        {
            string expectedPath = route.Disposition switch
            {
                OrchestratorProxyRouteDisposition.OrchestratorNative =>
                    NativeControllerPath(route.SourceDescriptor.Id),
                OrchestratorProxyRouteDisposition.OrchestratorReadinessOrMetaReplacement =>
                    ReplacementControllerPath(route.SourceDescriptor.Id),
                _ => throw new InvalidOperationException(),
            };
            string expected = Key(route.GatewayDescriptor.Method, expectedPath);
            if (!actual.Contains(expected))
                missing.Add($"{route.SourceName}:{route.SourceDescriptor.Id} -> {expected}");
        }

        Assert.True(missing.Count == 0,
            "Matrix routes missing an Orchestrator-native/replacement action:\n  - " +
            string.Join("\n  - ", missing));
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("apply")]
    public void Jira_public_people_maintenance_has_one_explicit_typed_proxy_disposition(string operation)
    {
        OrchestratorProxyRoute route = Assert.Single(OrchestratorProxyRouteMatrix.GetRoutes("jira"),
            route => route.SourceDescriptor.Id == $"public-people.{operation}");
        Assert.Equal(HttpMethod.Post, route.SourceDescriptor.Method);
        Assert.Equal(OrchestratorProxyRouteDisposition.TypedSourceProxy, route.Disposition);
        Assert.Equal($"api/v1/public-people/{operation}", route.SourceDescriptor.PathTemplate);
        Assert.Equal($"api/v1/jira/public-people/{operation}", route.GatewayDescriptor.PathTemplate);
        Assert.Equal(operation == "apply", route.GatewayDescriptor.Destructive);
        Assert.Contains("preview", route.GatewayDescriptor.Description!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Zulip_reference_resolution_has_one_read_only_typed_proxy_disposition()
    {
        OrchestratorProxyRoute route = Assert.Single(OrchestratorProxyRouteMatrix.GetRoutes("zulip"),
            route => route.SourceDescriptor.Id == "references.resolve");

        Assert.Equal(HttpMethod.Get, route.SourceDescriptor.Method);
        Assert.Equal(OrchestratorProxyRouteDisposition.TypedSourceProxy, route.Disposition);
        Assert.Equal("api/v1/references/resolve", route.SourceDescriptor.PathTemplate);
        Assert.Equal("api/v1/zulip/references/resolve", route.GatewayDescriptor.PathTemplate);
        Assert.False(route.GatewayDescriptor.Destructive);
        ApiParameter reference = Assert.Single(route.GatewayDescriptor.Parameters);
        Assert.Equal("reference", reference.Name);
        Assert.Equal(ApiParameterKind.Query, reference.Kind);
        Assert.True(reference.Required);
        Assert.Contains("Never fetches or ingests", route.GatewayDescriptor.Description);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void Source_tab_descriptors_build_on_the_orchestrator_origin(
        string sourceName,
        Assembly sourceAssembly,
        Func<IReadOnlyList<ApiEndpointDescriptor>> buildCatalog,
        Type proxyController)
    {
        _ = sourceAssembly;
        _ = buildCatalog;
        _ = proxyController;
        const string orchestratorBase = "http://orchestrator:5150";

        foreach (ApiEndpointDescriptor descriptor in SourceApiCatalog.GetCatalog(sourceName))
        {
            Dictionary<string, string?> values = descriptor.Parameters.ToDictionary(
                parameter => parameter.Name,
                parameter => (string?)(parameter.DefaultValue ?? "1"),
                StringComparer.OrdinalIgnoreCase);

            ApiBuiltRequest request = ApiUrlBuilder.Build(orchestratorBase, descriptor, values);

            Assert.StartsWith(orchestratorBase + "/api/v1/", request.Url, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Merged_openapi_document_exposes_every_matrix_gateway_route()
    {
        Assembly orchestratorAssembly =
            typeof(FhirAugury.Orchestrator.Controllers.ContentController).Assembly;
        OpenApiDocument orchestratorDocument = BuildOpenApiDocument(
            CatalogCoverageTests.EnumerateControllerRoutes(orchestratorAssembly),
            "orchestrator");
        Dictionary<string, OpenApiDocument?> sourceDocuments =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["jira"] = BuildOpenApiDocument(
                    CatalogCoverageTests.EnumerateControllerRoutes(
                        typeof(FhirAugury.Source.Jira.Controllers.ItemsController).Assembly),
                    "jira"),
                ["zulip"] = BuildOpenApiDocument(
                    CatalogCoverageTests.EnumerateControllerRoutes(
                        typeof(FhirAugury.Source.Zulip.Controllers.ItemsController).Assembly),
                    "zulip"),
                ["confluence"] = BuildOpenApiDocument(
                    CatalogCoverageTests.EnumerateControllerRoutes(
                        typeof(FhirAugury.Source.Confluence.Controllers.ItemsController).Assembly),
                    "confluence"),
                ["github"] = BuildOpenApiDocument(
                    CatalogCoverageTests.EnumerateControllerRoutes(
                        typeof(FhirAugury.Source.GitHub.Controllers.ItemsController).Assembly),
                    "github"),
                ["fhir"] = BuildOpenApiDocument(
                    CatalogCoverageTests.EnumerateControllerRoutes(
                        typeof(FhirAugury.Source.Fhir.Controllers.ReleasesController).Assembly),
                    "fhir"),
            };

        OpenApiDocument merged = OpenApiMerger.Merge(
            orchestratorDocument,
            sourceDocuments,
            includeInternal: false);
        using JsonDocument json = Serialize(merged);

        List<string> missing = [];
        foreach (OrchestratorProxyRoute route in OrchestratorProxyRouteMatrix.Routes)
        {
            OpenApiOperationInfo? operation = OpenApiCatalogClient.FindOperation(
                json,
                route.GatewayDescriptor.PathTemplate,
                route.GatewayDescriptor.Method);
            if (operation is null)
            {
                missing.Add(
                    $"{route.SourceName}:{route.SourceDescriptor.Id} -> " +
                    $"{route.GatewayDescriptor.Method.Method} {route.GatewayDescriptor.PathTemplate}");
            }
        }

        Assert.True(missing.Count == 0,
            "Merged OpenAPI routes missing matrix selections:\n  - " +
            string.Join("\n  - ", missing));
    }

    private static string NativeControllerPath(string descriptorId) => descriptorId switch
    {
        "content.search" => "api/v1/content/search",
        "content.get-item" => "api/v1/content/item/{source}/{**id}",
        "content.keywords" => "api/v1/content/keywords/{source}/{**id}",
        _ => throw new InvalidOperationException($"Unknown native descriptor '{descriptorId}'."),
    };

    private static string ReplacementControllerPath(string descriptorId) => descriptorId switch
    {
        "lifecycle.health" or "lifecycle.status" => "api/v1/services",
        "lifecycle.stats" => "api/v1/stats",
        _ => throw new InvalidOperationException($"Unknown replacement descriptor '{descriptorId}'."),
    };

    private static string PrefixSourcePath(string sourceName, string path)
    {
        const string ApiV1 = "api/v1";
        string remainder = path.StartsWith(ApiV1, StringComparison.Ordinal)
            ? path[ApiV1.Length..]
            : "/" + path.TrimStart('/');
        return $"api/v1/{sourceName}/{remainder.TrimStart('/')}";
    }

    private static IEnumerable<(HttpMethod Method, string Route)> EnumerateControllerRoutes(Type type)
    {
        string? routePrefix = type.GetCustomAttribute<RouteAttribute>()?.Template;
        if (routePrefix is null)
            yield break;

        foreach (MethodInfo methodInfo in type.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            foreach (HttpMethodAttribute attribute in methodInfo.GetCustomAttributes<HttpMethodAttribute>())
            {
                string template = string.IsNullOrEmpty(attribute.Template)
                    ? routePrefix
                    : $"{routePrefix.TrimEnd('/')}/{attribute.Template.TrimStart('/')}";
                yield return (new HttpMethod(attribute.HttpMethods.Single()), template);
            }
        }
    }

    private static string Key(HttpMethod method, string path) =>
        $"{method.Method} {CatalogCoverageTests.Normalize(path.Split('?', 2)[0])}";

    private static OpenApiDocument BuildOpenApiDocument(
        IEnumerable<(HttpMethod Method, string Route)> routes,
        string operationPrefix)
    {
        OpenApiDocument document = new()
        {
            Info = new OpenApiInfo { Title = operationPrefix, Version = "1.0.0" },
            Paths = [],
            Components = new OpenApiComponents
            {
                Schemas = new Dictionary<string, IOpenApiSchema>(),
            },
        };

        int operationIndex = 0;
        foreach ((HttpMethod method, string route) in routes)
        {
            string path = "/" + CatalogCoverageTests.Normalize(route);
            if (!document.Paths.TryGetValue(path, out IOpenApiPathItem? existing)
                || existing is not OpenApiPathItem pathItem)
            {
                pathItem = new OpenApiPathItem();
                document.Paths[path] = pathItem;
            }

            pathItem.Operations ??= new Dictionary<HttpMethod, OpenApiOperation>();
            pathItem.Operations[method] = new OpenApiOperation
            {
                OperationId = $"{operationPrefix}-operation-{operationIndex++}",
                Responses = new OpenApiResponses
                {
                    ["200"] = new OpenApiResponse { Description = "OK" },
                },
            };
        }

        return document;
    }

    private static JsonDocument Serialize(OpenApiDocument document)
    {
        using MemoryStream stream = new();
        using (StreamWriter textWriter = new(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            OpenApiJsonWriter writer = new(textWriter);
            document.SerializeAsV31(writer);
        }

        return JsonDocument.Parse(stream.ToArray());
    }
}
