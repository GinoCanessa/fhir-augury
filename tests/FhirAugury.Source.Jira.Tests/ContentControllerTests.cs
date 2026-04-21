using System.Reflection;
using FhirAugury.Source.Jira.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Jira.Tests;

/// <summary>
/// Phase A route-shape contract: the <c>content/item</c>,
/// <c>content/keywords</c>, and <c>content/related-by-keyword</c> templates
/// MUST use the catch-all token <c>{**id}</c> so that ids containing
/// embedded <c>/</c> (e.g. <c>FHIR-1234/sub</c>) are preserved verbatim.
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
        // Combined effective template: api/v1/content + item/{source}/{**id}
        // Sanity-check a representative slash-bearing id round-trips through
        // our simple template substitution (mirrors what ASP.NET routing does
        // for catch-all parameters).
        string controllerPrefix = typeof(ContentController)
            .GetCustomAttribute<Microsoft.AspNetCore.Mvc.RouteAttribute>()!.Template!;
        string actionTemplate = typeof(ContentController).GetMethod(nameof(ContentController.GetItem))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template!;

        string effective = $"{controllerPrefix}/{actionTemplate}"
            .Replace("{source}", "jira")
            .Replace("{**id}", "FHIR-1234/sub");

        Assert.Equal("api/v1/content/item/jira/FHIR-1234/sub", effective);
    }
}

