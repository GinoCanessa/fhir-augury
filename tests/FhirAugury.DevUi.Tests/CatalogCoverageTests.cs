using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using FhirAugury.DevUi.Services.ApiCatalog;
using FhirAugury.DevUi.Services.ApiCatalog.Catalogs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace FhirAugury.DevUi.Tests;

public class CatalogCoverageTests
{
    public static IEnumerable<object[]> Cases() =>
    [
        ["Jira", typeof(FhirAugury.Source.Jira.Controllers.ItemsController).Assembly,
            (Func<IReadOnlyList<ApiEndpointDescriptor>>)JiraCatalog.Build],
        ["Zulip", typeof(FhirAugury.Source.Zulip.Controllers.ItemsController).Assembly,
            (Func<IReadOnlyList<ApiEndpointDescriptor>>)ZulipCatalog.Build],
        ["GitHub", typeof(FhirAugury.Source.GitHub.Controllers.ItemsController).Assembly,
            (Func<IReadOnlyList<ApiEndpointDescriptor>>)GitHubCatalog.Build],
        ["Confluence", typeof(FhirAugury.Source.Confluence.Controllers.ItemsController).Assembly,
            (Func<IReadOnlyList<ApiEndpointDescriptor>>)ConfluenceCatalog.Build],
        ["Fhir", typeof(FhirAugury.Source.Fhir.Controllers.ReleasesController).Assembly,
            (Func<IReadOnlyList<ApiEndpointDescriptor>>)FhirCatalog.Build],
        ["Orchestrator", typeof(FhirAugury.Orchestrator.Controllers.ContentController).Assembly,
            (Func<IReadOnlyList<ApiEndpointDescriptor>>)OrchestratorCatalog.Build],
    ];

    public static IEnumerable<object[]> SourceCases() =>
    [
        ["jira", (Func<IReadOnlyList<ApiEndpointDescriptor>>)JiraCatalog.Build],
        ["zulip", (Func<IReadOnlyList<ApiEndpointDescriptor>>)ZulipCatalog.Build],
        ["github", (Func<IReadOnlyList<ApiEndpointDescriptor>>)GitHubCatalog.Build],
        ["confluence", (Func<IReadOnlyList<ApiEndpointDescriptor>>)ConfluenceCatalog.Build],
        ["fhir", (Func<IReadOnlyList<ApiEndpointDescriptor>>)FhirCatalog.Build],
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Catalog_covers_every_controller_route(
        string sourceName, Assembly sourceAssembly, Func<IReadOnlyList<ApiEndpointDescriptor>> buildCatalog)
    {
        HashSet<string> catalogRoutes = buildCatalog()
            .Select(d => $"{d.Method.Method} {Normalize(d.PathTemplate)}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> missing = [];
        foreach ((HttpMethod method, string route) in EnumerateControllerRoutes(sourceAssembly))
        {
            string key = $"{method.Method} {Normalize(route)}";
            if (!catalogRoutes.Contains(key))
                missing.Add(key);
        }

        Assert.True(missing.Count == 0,
            $"{sourceName} catalog is missing the following controller routes:\n  - " +
            string.Join("\n  - ", missing));
    }

    [Theory]
    [MemberData(nameof(SourceCases))]
    public void Gateway_source_catalog_has_one_descriptor_for_every_matrix_entry(
        string sourceName,
        Func<IReadOnlyList<ApiEndpointDescriptor>> buildSourceCatalog)
    {
        IReadOnlyList<ApiEndpointDescriptor> sourceCatalog = buildSourceCatalog();
        IReadOnlyList<OrchestratorProxyRoute> matrix = OrchestratorProxyRouteMatrix.GetRoutes(sourceName);
        IReadOnlyList<ApiEndpointDescriptor> gatewayCatalog = SourceApiCatalog.GetCatalog(sourceName);

        Assert.Equal(sourceCatalog.Count, matrix.Count);
        Assert.Equal(matrix.Count, gatewayCatalog.Count);

        for (int i = 0; i < sourceCatalog.Count; i++)
        {
            OrchestratorProxyRoute route = matrix[i];
            ApiEndpointDescriptor gateway = gatewayCatalog[i];

            Assert.Equal(sourceName, route.SourceName, ignoreCase: true);
            Assert.Equal(sourceCatalog[i].Id, route.SourceDescriptor.Id);
            Assert.Equal(sourceCatalog[i].Method, route.SourceDescriptor.Method);
            Assert.Equal(sourceCatalog[i].PathTemplate, route.SourceDescriptor.PathTemplate);
            Assert.Equal(route.GatewayDescriptor, gateway);
        }
    }

    internal static IEnumerable<(HttpMethod Method, string Route)> EnumerateControllerRoutes(Assembly assembly)
    {
        foreach (Type t in assembly.GetTypes())
        {
            if (!typeof(ControllerBase).IsAssignableFrom(t)) continue;
            string? routePrefix = t.GetCustomAttribute<RouteAttribute>()?.Template;
            if (routePrefix is null) continue;

            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                IEnumerable<HttpMethodAttribute> attrs = m.GetCustomAttributes<HttpMethodAttribute>();
                foreach (HttpMethodAttribute attr in attrs)
                {
                    string method = attr.HttpMethods.First();
                    string template = string.IsNullOrEmpty(attr.Template)
                        ? routePrefix
                        : $"{routePrefix.TrimEnd('/')}/{attr.Template.TrimStart('/')}";
                    yield return (new HttpMethod(method), template);
                }
            }
        }
    }

    /// <summary>
    /// Normalize ASP.NET route templates so catalogs and controllers compare equal:
    /// strips constraint suffixes (<c>{id:int}</c> → <c>{id}</c>) and the catch-all
    /// stars (<c>{*id}</c>, <c>{**id}</c> → <c>{id}</c>), then lowercases.
    /// </summary>
    internal static string Normalize(string template) =>
        Regex.Replace(template, @"\{(\*{1,2})?([A-Za-z_][A-Za-z0-9_]*)(?::[^}]*)?\}", "{$2}")
             .Trim('/').ToLowerInvariant();
}
