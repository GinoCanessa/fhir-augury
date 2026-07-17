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
    public async Task ThreadSnapshot_ForwardsQueryStringVerbatim()
    {
        ZulipProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, queryString: "?streamName=FHIR%20Infrastructure&topic=general%20topic");

        IActionResult r = await c.GetThreadSnapshot("FHIR Infrastructure", "general topic", default);
        await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Equal("/api/v1/threads/snapshot", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?streamName=FHIR%20Infrastructure&topic=general%20topic", h.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task GetStreamTopics_PreservesQueryString()
    {
        ZulipProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, queryString: "?streamName=implementers&limit=10&offset=20");

        IActionResult r = await c.GetStreamTopics("implementers", 10, 20, default);
        await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Equal("/api/v1/streams/topics", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?streamName=implementers&limit=10&offset=20", h.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task GetStreamTopics_ForwardsEncodedSlashVerbatim()
    {
        ZulipProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, queryString: "?streamName=fhir%2Finfrastructure-wg&limit=5");

        IActionResult r = await c.GetStreamTopics("fhir/infrastructure-wg", 5, null, default);
        await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Equal("/api/v1/streams/topics", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?streamName=fhir%2Finfrastructure-wg&limit=5", h.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task GetThread_ForwardsEncodedSlashVerbatim()
    {
        ZulipProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, queryString: "?streamName=fhir%2Finfrastructure-wg&topic=Message%20forbids");

        IActionResult r = await c.GetThread("fhir/infrastructure-wg", "Message forbids", null, default);
        await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Equal("/api/v1/threads", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?streamName=fhir%2Finfrastructure-wg&topic=Message%20forbids", h.Requests[0].RequestUri!.Query);
    }
}
