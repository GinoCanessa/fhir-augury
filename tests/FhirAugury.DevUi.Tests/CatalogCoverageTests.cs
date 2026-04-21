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

    private static IEnumerable<(HttpMethod Method, string Route)> EnumerateControllerRoutes(Assembly assembly)
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
