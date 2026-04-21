using System.Reflection;
using FhirAugury.Source.Zulip.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Zulip.Tests;

/// <summary>
/// Phase A route-shape contract: <c>content/item</c>, <c>content/keywords</c>,
/// and <c>content/related-by-keyword</c> use the catch-all <c>{**id}</c> so
/// embedded <c>/</c> in ids round-trips through routing without being split.
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

        string effective = $"{controllerPrefix}/{actionTemplate}"
            .Replace("{source}", "zulip")
            .Replace("{**id}", "12345/extra-segment");

        Assert.Equal("api/v1/content/item/zulip/12345/extra-segment", effective);
    }
}

