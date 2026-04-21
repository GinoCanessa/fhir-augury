using System.Net;
using FhirAugury.Orchestrator.Controllers.Proxies;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Tests.Proxies;

public class JiraProxyControllerTests
{
    private static JiraProxyController NewController(out ProxyTestSupport.CapturingHandler handler,
        string responseBody = """{"ok":true}""",
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? responseEtag = null,
        bool enabled = true)
    {
        (SourceHttpClient client, ProxyTestSupport.CapturingHandler h) =
            ProxyTestSupport.CreateClient("jira", responseBody, statusCode, responseEtag, enabled);
        handler = h;
        return new JiraProxyController(client);
    }

    public static IEnumerable<object[]> SimpleGetCases =>
    [
        ["WorkGroups", "/api/v1/work-groups"],
        ["Statuses", "/api/v1/statuses"],
        ["Labels", "/api/v1/labels"],
        ["Users", "/api/v1/users"],
        ["InPersons", "/api/v1/inpersons"],
        ["ListSpecifications", "/api/v1/specifications"],
        ["AllWorkGroupIssues", "/api/v1/work-groups/issues"],
        ["ListProjects", "/api/v1/projects"],
    ];

    [Theory]
    [MemberData(nameof(SimpleGetCases))]
    public async Task TrivialGets_ForwardCorrectPath(string action, string expectedPath)
    {
        JiraProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);

        IActionResult result = action switch
        {
            "WorkGroups" => await c.WorkGroups(default),
            "Statuses" => await c.Statuses(default),
            "Labels" => await c.Labels(default),
            "Users" => await c.Users(default),
            "InPersons" => await c.InPersons(default),
            "ListSpecifications" => await c.ListSpecifications(default),
            "AllWorkGroupIssues" => await c.AllWorkGroupIssues(default),
            "ListProjects" => await c.ListProjects(default),
            _ => throw new InvalidOperationException(),
        };
        (int status, string body, _, _) = await ProxyTestSupport.ExecuteAsync(c, result);

        Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Get, h.Requests[0].Method);
        Assert.Equal(expectedPath, h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(200, status);
        Assert.Contains("ok", body);
    }

    [Fact]
    public async Task GetItem_PreservesQueryString()
    {
        JiraProxyController c = NewController(out ProxyTestSupport.CapturingHandler h,
            responseBody: """{"id":"FHIR-1","title":"x"}""");
        ProxyTestSupport.SetRequest(c, queryString: "?includeContent=true&includeComments=false");

        IActionResult r = await c.GetItem("FHIR-1", true, false, default);
        (int status, string body, _, _) = await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Single(h.Requests);
        Assert.Equal("/api/v1/items/FHIR-1", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?includeContent=true&includeComments=false", h.Requests[0].RequestUri!.Query);
        Assert.Equal(200, status);
        Assert.Contains("FHIR-1", body);
    }

    [Fact]
    public async Task QueryProxy_ForwardsRequestBody()
    {
        JiraProxyController c = NewController(out ProxyTestSupport.CapturingHandler h,
            responseBody: """{"results":[{"key":"FHIR-100"}]}""");
        ProxyTestSupport.SetRequest(c, method: "POST",
            body: """{"statuses":["Triaged"],"limit":10}""");

        IActionResult r = await c.Query(default);
        (int status, string body, _, _) = await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, h.Requests[0].Method);
        Assert.Equal("/api/v1/query", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("Triaged", h.Bodies[0]);
        Assert.Contains("FHIR-100", body);
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task UpdateProject_PutForwardsBody()
    {
        JiraProxyController c = NewController(out ProxyTestSupport.CapturingHandler h,
            responseBody: """{"updated":true}""");
        ProxyTestSupport.SetRequest(c, method: "PUT",
            body: """{"displayName":"FHIR Core","enabled":true}""");

        IActionResult r = await c.UpdateProject("FHIR", default);
        (int status, string body, _, _) = await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Put, h.Requests[0].Method);
        Assert.Equal("/api/v1/projects/FHIR", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("FHIR Core", h.Bodies[0]);
        Assert.Contains("updated", body);
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task Ingest_WithJiraProject_ForwardsAsProjectQueryParam()
    {
        JiraProxyController c = NewController(out ProxyTestSupport.CapturingHandler h,
            responseBody: """{"queued":true}""");
        ProxyTestSupport.SetRequest(c, method: "POST", queryString: "?type=full&jira-project=FHIR");

        IActionResult r = await c.Ingest("full", "FHIR", default);
        (int status, _, _, _) = await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, h.Requests[0].Method);
        Assert.Equal("/api/v1/ingest", h.Requests[0].RequestUri!.AbsolutePath);
        // Source-facing query string uses ?project=, not ?jira-project=
        string query = h.Requests[0].RequestUri!.Query;
        Assert.Contains("type=full", query);
        Assert.Contains("project=FHIR", query);
        Assert.DoesNotContain("jira-project", query);
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task IngestTrigger_WithoutJiraProject_OmitsProject()
    {
        JiraProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, method: "POST", queryString: "?type=incremental");

        IActionResult r = await c.IngestTrigger("incremental", null, default);
        await ProxyTestSupport.ExecuteAsync(c, r);

        string q = h.Requests[0].RequestUri!.Query;
        Assert.Contains("type=incremental", q);
        Assert.DoesNotContain("project", q);
    }

    [Fact]
    public async Task Disabled_Returns404()
    {
        JiraProxyController c = NewController(out ProxyTestSupport.CapturingHandler h, enabled: false);
        ProxyTestSupport.SetRequest(c);

        IActionResult r = await c.WorkGroups(default);

        NotFoundObjectResult nf = Assert.IsType<NotFoundObjectResult>(r);
        Assert.Equal(404, nf.StatusCode);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task GetItem_IfNoneMatch_RoundTrips304WithEtag()
    {
        JiraProxyController c = NewController(out ProxyTestSupport.CapturingHandler h,
            responseBody: "",
            statusCode: HttpStatusCode.NotModified,
            responseEtag: "\"abc123\"");
        ProxyTestSupport.SetRequest(c,
            headers: new Dictionary<string, string> { ["If-None-Match"] = "\"abc123\"" });

        IActionResult r = await c.GetItem("FHIR-1", null, null, default);
        (int status, string body, string? etag, _) = await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Single(h.Requests);
        Assert.True(h.Requests[0].Headers.IfNoneMatch.Any(),
            "Expected If-None-Match header to be forwarded upstream");
        Assert.Equal("\"abc123\"", h.Requests[0].Headers.IfNoneMatch.First().Tag);
        Assert.Equal(304, status);
        Assert.Empty(body);
        Assert.Equal("\"abc123\"", etag);
    }
}
