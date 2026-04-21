using System.Net;
using FhirAugury.Orchestrator.Controllers.Proxies;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Tests.Proxies;

public class ZulipProxyControllerTests
{
    private static ZulipProxyController NewController(out ProxyTestSupport.CapturingHandler handler,
        string responseBody = """{"ok":true}""",
        bool enabled = true)
    {
        (SourceHttpClient client, ProxyTestSupport.CapturingHandler h) =
            ProxyTestSupport.CreateClient("zulip", responseBody, HttpStatusCode.OK, enabled: enabled);
        handler = h;
        return new ZulipProxyController(client);
    }

    public static IEnumerable<object[]> SimpleGetCases =>
    [
        ["ListStreams", "/api/v1/streams"],
        ["ListMessages", "/api/v1/messages"],
        ["ListItems", "/api/v1/items"],
    ];

    [Theory]
    [MemberData(nameof(SimpleGetCases))]
    public async Task TrivialGets(string action, string expectedPath)
    {
        ZulipProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);

        IActionResult r = action switch
        {
            "ListStreams" => await c.ListStreams(default),
            "ListMessages" => await c.ListMessages(null, null, default),
            "ListItems" => await c.ListItems(null, null, default),
            _ => throw new InvalidOperationException(),
        };
        (int status, _, _, _) = await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Single(h.Requests);
        Assert.Equal(expectedPath, h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task GetMessage_IntRoute_ForwardsId()
    {
        ZulipProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);

        IActionResult r = await c.GetMessage(42, default);
        await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Equal("/api/v1/messages/42", h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task UpdateStream_PutForwardsBody()
    {
        ZulipProxyController c = NewController(out ProxyTestSupport.CapturingHandler h,
            responseBody: """{"updated":true}""");
        ProxyTestSupport.SetRequest(c, method: "PUT",
            body: """{"description":"new desc"}""");

        IActionResult r = await c.UpdateStream(7, default);
        (int status, _, _, _) = await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Put, h.Requests[0].Method);
        Assert.Equal("/api/v1/streams/7", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("new desc", h.Bodies[0]);
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task Query_PostForwardsBody()
    {
        ZulipProxyController c = NewController(out ProxyTestSupport.CapturingHandler h,
            responseBody: """{"total":1}""");
        ProxyTestSupport.SetRequest(c, method: "POST", body: """{"streamNames":["implementers"]}""");

        IActionResult r = await c.Query(default);
        await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, h.Requests[0].Method);
        Assert.Equal("/api/v1/query", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("implementers", h.Bodies[0]);
    }

    [Fact]
    public async Task ThreadSnapshot_TwoSegmentRoute_ForwardsBoth()
    {
        ZulipProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);

        IActionResult r = await c.GetThreadSnapshot("FHIR Infrastructure", "general topic", default);
        await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Equal("/api/v1/threads/FHIR%20Infrastructure/general%20topic/snapshot",
            h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetStreamTopics_PreservesQueryString()
    {
        ZulipProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, queryString: "?limit=10&offset=20");

        IActionResult r = await c.GetStreamTopics("implementers", 10, 20, default);
        await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Equal("/api/v1/streams/implementers/topics", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?limit=10&offset=20", h.Requests[0].RequestUri!.Query);
    }
}
