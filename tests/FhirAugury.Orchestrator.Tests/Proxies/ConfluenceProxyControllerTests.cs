using System.Net;
using FhirAugury.Orchestrator.Controllers.Proxies;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Tests.Proxies;

public class ConfluenceProxyControllerTests
{
    private static ConfluenceProxyController NewController(out ProxyTestSupport.CapturingHandler handler,
        string responseBody = """{"ok":true}""")
    {
        (SourceHttpClient client, ProxyTestSupport.CapturingHandler h) =
            ProxyTestSupport.CreateClient("confluence", responseBody);
        handler = h;
        return new ConfluenceProxyController(client);
    }

    public static IEnumerable<object[]> PageSubResources =>
    [
        ["GetPage", "/api/v1/pages/p1"],
        ["GetPageRelated", "/api/v1/pages/p1/related"],
        ["GetPageSnapshot", "/api/v1/pages/p1/snapshot"],
        ["GetPageContent", "/api/v1/pages/p1/content"],
        ["GetPageComments", "/api/v1/pages/p1/comments"],
        ["GetPageChildren", "/api/v1/pages/p1/children"],
        ["GetPageAncestors", "/api/v1/pages/p1/ancestors"],
        ["GetPageLinked", "/api/v1/pages/p1/linked"],
    ];

    [Theory]
    [MemberData(nameof(PageSubResources))]
    public async Task PageSubResources_ForwardCorrectPath(string action, string expectedPath)
    {
        ConfluenceProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);

        IActionResult r = action switch
        {
            "GetPage" => await c.GetPage("p1", default),
            "GetPageRelated" => await c.GetPageRelated("p1", null, default),
            "GetPageSnapshot" => await c.GetPageSnapshot("p1", default),
            "GetPageContent" => await c.GetPageContent("p1", null, default),
            "GetPageComments" => await c.GetPageComments("p1", default),
            "GetPageChildren" => await c.GetPageChildren("p1", default),
            "GetPageAncestors" => await c.GetPageAncestors("p1", default),
            "GetPageLinked" => await c.GetPageLinked("p1", null, default),
            _ => throw new InvalidOperationException(),
        };
        await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Single(h.Requests);
        Assert.Equal(expectedPath, h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PagesByLabel_PreservesQueryString()
    {
        ConfluenceProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, queryString: "?spaceKey=FHIR&limit=20");

        IActionResult r = await c.PagesByLabel("design", "FHIR", 20, null, default);
        await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Equal("/api/v1/pages/by-label/design", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?spaceKey=FHIR&limit=20", h.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task ListSpaces_ForwardsGet()
    {
        ConfluenceProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);
        IActionResult r = await c.ListSpaces(default);
        await ProxyTestSupport.ExecuteAsync(c, r);
        Assert.Equal("/api/v1/spaces", h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Ingest_PostForwards()
    {
        ConfluenceProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, method: "POST", queryString: "?type=full");
        IActionResult r = await c.Ingest("full", default);
        await ProxyTestSupport.ExecuteAsync(c, r);
        Assert.Equal(HttpMethod.Post, h.Requests[0].Method);
        Assert.Equal("/api/v1/ingest", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?type=full", h.Requests[0].RequestUri!.Query);
    }
}
