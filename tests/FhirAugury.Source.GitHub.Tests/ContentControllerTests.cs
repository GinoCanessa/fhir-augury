using System.Reflection;
using FhirAugury.Source.GitHub.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Phase A route-shape contract for the GitHub source. GitHub keys (e.g.
/// <c>owner/repo#123</c>, <c>owner/repo:path/to/file.json</c>) embed <c>/</c>;
/// the catch-all <c>{**id}</c> token is required so routing doesn't split
/// them into separate path segments.
/// </summary>
public class ContentControllerTests
{
    [Theory]
    [InlineData(nameof(ContentController.GetItem), "item/{source}/{**id}")]
    [InlineData(nameof(ContentController.GetKeywords), "keywords/{source}/{**id}")]
    [InlineData(nameof(ContentController.RelatedByKeyword), "related-by-keyword/{source}/{**id}")]
    public void RouteTemplate_UsesDoubleStarCatchAll_PreservesEmbeddedSlashes(
        string actionName, string expectedTemplate)
    {
        MethodInfo method = typeof(ContentController).GetMethod(actionName)!;
        HttpGetAttribute attr = method.GetCustomAttribute<HttpGetAttribute>()!;
        Assert.Equal(expectedTemplate, attr.Template);
    }

    [Fact]
    public void ControllerRoutePrefix_IsApiV1Content()
    {
        Microsoft.AspNetCore.Mvc.RouteAttribute attr =
            typeof(ContentController).GetCustomAttribute<Microsoft.AspNetCore.Mvc.RouteAttribute>()!;
        Assert.Equal("api/v1/content", attr.Template);
    }

    [Fact]
    public void EffectiveItemUrl_AllowsSlashInId()
    {
        string controllerPrefix = typeof(ContentController)
            .GetCustomAttribute<Microsoft.AspNetCore.Mvc.RouteAttribute>()!.Template!;
        string actionTemplate = typeof(ContentController).GetMethod(nameof(ContentController.GetItem))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template!;

        // GitHub file ids look like "owner/repo:path/to/file.json".
        string effective = $"{controllerPrefix}/{actionTemplate}"
            .Replace("{source}", "github")
            .Replace("{**id}", "owner/repo:path/to/file.json");

        Assert.Equal("api/v1/content/item/github/owner/repo:path/to/file.json", effective);
    }
}

