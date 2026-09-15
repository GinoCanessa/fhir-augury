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
    public async Task ContentRefersTo_PreservesSourceSpecificQuery()
    {
        ZulipProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, queryString: "?value=FHIR-1&sourceType=jira&limit=10");

        IActionResult result = await c.ContentRefersTo("FHIR-1", "jira", 10, null, default);
        await ProxyTestSupport.ExecuteAsync(c, result);

        Assert.Equal("/api/v1/content/refers-to", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?value=FHIR-1&sourceType=jira&limit=10", h.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task ContentRelatedByKeyword_PreservesCatchAllId()
    {
        ZulipProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);

        IActionResult result = await c.ContentRelatedByKeyword(
            "zulip", "1234/topic name", null, null, null, default);
        await ProxyTestSupport.ExecuteAsync(c, result);

        Assert.Equal(
            "/api/v1/content/related-by-keyword/zulip/1234/topic%20name",
            h.Requests[0].RequestUri!.AbsolutePath);
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

    [Theory]
    [InlineData("321987")]
    [InlineData("000321987")]
    [InlineData("fhir/infrastructure-wg:entry/request: réponse 100% & 漢字")]
    [InlineData("fhir:core:literal %2F / 🩺")]
    [InlineData("implementers: topic with surrounding spaces ")]
    public async Task ResolveReference_ForwardsEncodedReferenceVerbatim(string reference)
    {
        ZulipProxyController controller = NewController(out ProxyTestSupport.CapturingHandler handler);
        string query = $"?reference={Uri.EscapeDataString(reference)}";
        ProxyTestSupport.SetRequest(controller, queryString: query);

        IActionResult result = await controller.ResolveReference(reference, default);
        (int status, _, _, _) = await ProxyTestSupport.ExecuteAsync(controller, result);

        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/references/resolve", request.RequestUri!.AbsolutePath);
        Assert.Equal(query, request.RequestUri.Query);
        Assert.Equal(200, status);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ResolveReference_PreservesSourceFailureStatusAndBody(HttpStatusCode sourceStatus)
    {
        const string body = """{"reference":"321987","outcome":"source-unavailable","diagnostics":[]}""";
        (SourceHttpClient client, ProxyTestSupport.CapturingHandler handler) =
            ProxyTestSupport.CreateClient("zulip", body, sourceStatus, responseEtag: "\"reference-v1\"");
        ZulipProxyController controller = new(client);
        ProxyTestSupport.SetRequest(controller, queryString: "?reference=321987",
            headers: new Dictionary<string, string> { ["If-None-Match"] = "\"prior\"" });

        IActionResult result = await controller.ResolveReference("321987", default);
        (int status, string actualBody, string? etag, string? contentType) =
            await ProxyTestSupport.ExecuteAsync(controller, result);

        Assert.Equal((int)sourceStatus, status);
        Assert.Equal(body, actualBody);
        Assert.Equal("\"reference-v1\"", etag);
        Assert.StartsWith("application/json", contentType);
        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal("\"prior\"", Assert.Single(request.Headers.IfNoneMatch).Tag);
    }

    [Fact]
    public async Task ResolveReference_DisabledSourceDoesNotForward()
    {
        ZulipProxyController controller = NewController(out ProxyTestSupport.CapturingHandler handler, enabled: false);
        ProxyTestSupport.SetRequest(controller, queryString: "?reference=321987");

        IActionResult result = await controller.ResolveReference("321987", default);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("trigger", "/api/v1/ingest/trigger")]
    [InlineData("rebuild-index", "/api/v1/rebuild-index")]
    public async Task MissingIngestionRoutes_ForwardPost(string action, string expectedPath)
    {
        ZulipProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, method: "POST", queryString: "?type=full");

        IActionResult result = action switch
        {
            "trigger" => await c.IngestTrigger("full", default),
            "rebuild-index" => await c.RebuildIndex("full", default),
            _ => throw new InvalidOperationException(),
        };
        await ProxyTestSupport.ExecuteAsync(c, result);

        Assert.Equal(HttpMethod.Post, h.Requests[0].Method);
        Assert.Equal(expectedPath, h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?type=full", h.Requests[0].RequestUri!.Query);
    }
}
