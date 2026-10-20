using System.Net;
using System.Text.Json;
using FhirAugury.Common.Api;
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
        ["ListBalDef", "/api/v1/baldef"],
        ["ListBallot", "/api/v1/ballot"],
        ["ListPss", "/api/v1/pss"],
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
            "ListBalDef" => await c.ListBalDef(null, null, null, null, null, default),
            "ListBallot" => await c.ListBallot(null, null, null, null, null, default),
            "ListPss" => await c.ListPss(null, null, null, null, default),
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

    public static IEnumerable<object[]> ContentQueryCases =>
    [
        ["refers-to", "/api/v1/content/refers-to"],
        ["referred-by", "/api/v1/content/referred-by"],
        ["cross-referenced", "/api/v1/content/cross-referenced"],
    ];

    [Theory]
    [MemberData(nameof(ContentQueryCases))]
    public async Task ContentQueries_ForwardPathAndQuery(string action, string expectedPath)
    {
        JiraProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, queryString: "?value=FHIR-1&sourceType=github&limit=5");

        IActionResult result = action switch
        {
            "refers-to" => await c.ContentRefersTo("FHIR-1", "github", 5, null, default),
            "referred-by" => await c.ContentReferredBy("FHIR-1", "github", 5, null, default),
            "cross-referenced" => await c.ContentCrossReferenced("FHIR-1", "github", 5, null, default),
            _ => throw new InvalidOperationException(),
        };
        await ProxyTestSupport.ExecuteAsync(c, result);

        Assert.Equal(expectedPath, h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?value=FHIR-1&sourceType=github&limit=5", h.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task ContentRelatedByKeyword_ForwardsFixedSourceAndCatchAllId()
    {
        JiraProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, queryString: "?minScore=0.2");

        IActionResult result = await c.ContentRelatedByKeyword(
            "jira", "FHIR-1/child", 0.2, null, null, default);
        await ProxyTestSupport.ExecuteAsync(c, result);

        Assert.Equal(
            "/api/v1/content/related-by-keyword/jira/FHIR-1/child",
            h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?minScore=0.2", h.Requests[0].RequestUri!.Query);
    }

    public static IEnumerable<object[]> ByKeyCases =>
    [
        ["baldef", "BALDEF-1", "/api/v1/baldef/BALDEF-1"],
        ["ballot", "BALLOT-1", "/api/v1/ballot/BALLOT-1"],
        ["pss", "PSS-1", "/api/v1/pss/PSS-1"],
    ];

    [Theory]
    [MemberData(nameof(ByKeyCases))]
    public async Task ReadModelByKey_ForwardsCorrectPath(string model, string key, string expectedPath)
    {
        JiraProxyController c = NewController(out ProxyTestSupport.CapturingHandler h,
            responseBody: $$"""{"key":"{{key}}"}""");
        ProxyTestSupport.SetRequest(c);

        IActionResult r = model switch
        {
            "baldef" => await c.GetBalDef(key, default),
            "ballot" => await c.GetBallot(key, default),
            "pss" => await c.GetPss(key, default),
            _ => throw new InvalidOperationException(),
        };
        (int status, string body, _, _) = await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Get, h.Requests[0].Method);
        Assert.Equal(expectedPath, h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(200, status);
        Assert.Contains(key, body);
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
    public async Task LocalProcessingSelectionTickets_ForwardsBodyQueryAndResponse()
    {
        const string response = """
            {
              "results":[{"key":"FHIR-2","projectKey":"FHIR","title":"Selected ticket"}],
              "limit":1,
              "offset":1,
              "total":3,
              "provenance":{
                "source":"jira",
                "contentRevision":42,
                "isStable":true,
                "projectLastSuccessfulRefreshAt":{"FHIR":"2026-09-30T12:00:00+00:00","UNKNOWN":null}
              }
            }
            """;
        JiraLocalProcessingSelectionRequest request = new()
        {
            Projects = ["FHIR"],
            Statuses = ["Open"],
            Labels = ["exact-label"],
            ProcessedLocally = null,
            LabelText = new JiraLabelTextFilter
            {
                Includes = [" inc-01 ", "x' OR 1=1 --", @"path\label", "%", "_", " inc-01 "],
                Excludes = ["ex-01", "ex-02"],
            },
            Keys = ["FHIR-1", "FHIR-2"],
            Limit = 1,
            Offset = 1,
        };
        string requestBody = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        JiraProxyController controller = NewController(out ProxyTestSupport.CapturingHandler handler, responseBody: response);
        ProxyTestSupport.SetRequest(controller, method: "POST", queryString: "?type=fhir", body: requestBody);

        HttpPostAttribute route = Assert.Single(
            typeof(JiraProxyController).GetMethods()
                .Single(method => method.Name == nameof(JiraProxyController.LocalProcessingSelectionTickets))
                .GetCustomAttributes(typeof(HttpPostAttribute), inherit: false)
                .Cast<HttpPostAttribute>());
        Assert.Equal("local-processing/selection-tickets", route.Template);

        IActionResult result = await controller.LocalProcessingSelectionTickets(default);
        (int status, string body, _, string? contentType) = await ProxyTestSupport.ExecuteAsync(controller, result);

        HttpRequestMessage forwarded = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, forwarded.Method);
        Assert.Equal("/api/v1/local-processing/selection-tickets", forwarded.RequestUri?.AbsolutePath);
        Assert.Equal("?type=fhir", forwarded.RequestUri?.Query);
        Assert.Equal("application/json", forwarded.Content?.Headers.ContentType?.MediaType);
        string forwardedBody = Assert.IsType<string>(Assert.Single(handler.Bodies));
        Assert.Equal(requestBody, forwardedBody);
        using JsonDocument sentJson = JsonDocument.Parse(forwardedBody);
        JsonElement sent = sentJson.RootElement;
        Assert.Equal(request.LabelText.Includes, sent.GetProperty("labelText").GetProperty("includes").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(request.LabelText.Excludes, sent.GetProperty("labelText").GetProperty("excludes").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(request.Keys, sent.GetProperty("keys").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(request.Labels, sent.GetProperty("labels").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(request.Projects, sent.GetProperty("projects").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(request.Statuses, sent.GetProperty("statuses").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("processedLocally").ValueKind);
        Assert.Equal(1, sent.GetProperty("limit").GetInt32());
        Assert.Equal(1, sent.GetProperty("offset").GetInt32());

        Assert.Equal(200, status);
        Assert.Equal(response, body);
        Assert.NotNull(contentType);
        Assert.StartsWith("application/json", contentType);
        using JsonDocument responseJson = JsonDocument.Parse(body);
        JsonElement received = responseJson.RootElement;
        Assert.Equal("FHIR-2", Assert.Single(received.GetProperty("results").EnumerateArray()).GetProperty("key").GetString());
        Assert.Equal(1, received.GetProperty("limit").GetInt32());
        Assert.Equal(1, received.GetProperty("offset").GetInt32());
        Assert.Equal(3, received.GetProperty("total").GetInt32());
        JsonElement provenance = received.GetProperty("provenance");
        Assert.Equal("jira", provenance.GetProperty("source").GetString());
        Assert.Equal(42, provenance.GetProperty("contentRevision").GetInt64());
        Assert.True(provenance.GetProperty("isStable").GetBoolean());
        Assert.Equal(
            "2026-09-30T12:00:00+00:00",
            provenance.GetProperty("projectLastSuccessfulRefreshAt").GetProperty("FHIR").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            provenance.GetProperty("projectLastSuccessfulRefreshAt").GetProperty("UNKNOWN").ValueKind);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(503)]
    public async Task LocalProcessingSelectionTickets_PreservesFailureStatus(int upstreamStatus)
    {
        const string request = """{"labelText":{"includes":["inc-01"],"excludes":["ex-01"]},"keys":["FHIR-1"],"limit":500,"offset":0}""";
        const string response = """{"error":"source-selection-error","detail":"source diagnostic"}""";
        JiraProxyController controller = NewController(
            out ProxyTestSupport.CapturingHandler handler,
            responseBody: response,
            statusCode: (HttpStatusCode)upstreamStatus);
        ProxyTestSupport.SetRequest(controller, method: "POST", queryString: "?type=fhir", body: request);

        IActionResult result = await controller.LocalProcessingSelectionTickets(default);
        (int status, string body, _, _) = await ProxyTestSupport.ExecuteAsync(controller, result);

        HttpRequestMessage forwarded = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, forwarded.Method);
        Assert.Equal("/api/v1/local-processing/selection-tickets", forwarded.RequestUri?.AbsolutePath);
        Assert.Equal("?type=fhir", forwarded.RequestUri?.Query);
        Assert.Equal(request, Assert.Single(handler.Bodies));
        Assert.Equal(upstreamStatus, status);
        Assert.Equal(response, body);
        using JsonDocument json = JsonDocument.Parse(body);
        Assert.Equal("source-selection-error", json.RootElement.GetProperty("error").GetString());
        Assert.False(json.RootElement.TryGetProperty("results", out _));
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

    [Theory]
    [InlineData("preview", 200)]
    [InlineData("preview", 400)]
    [InlineData("preview", 409)]
    [InlineData("preview", 503)]
    [InlineData("apply", 200)]
    [InlineData("apply", 409)]
    [InlineData("apply", 410)]
    [InlineData("apply", 503)]
    public async Task PublicPeople_ForwardsBodyAndPreservesSourceFailures(string operation, int upstreamStatus)
    {
        const string response = """{"code":"source-safe-result","contentRevision":14}""";
        string request = operation == "preview"
            ? """{"keys":["FHIR-1"],"evidenceMode":"cache-only"}"""
            : """{"previewToken":"opaque-token","acknowledgeSharedUserImpact":true}""";
        JiraProxyController controller = NewController(out ProxyTestSupport.CapturingHandler handler,
            responseBody: response, statusCode: (HttpStatusCode)upstreamStatus);
        ProxyTestSupport.SetRequest(controller, method: "POST", body: request);

        IActionResult result = operation == "preview"
            ? await controller.PreviewPublicPeople(default)
            : await controller.ApplyPublicPeople(default);
        (int status, string body, _, _) = await ProxyTestSupport.ExecuteAsync(controller, result);

        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal($"/api/v1/public-people/{operation}", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(request, handler.Bodies[0]);
        Assert.Equal(upstreamStatus, status);
        Assert.Equal(response, body);
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
    public async Task Ingest_AcceptsSourceLocalProjectQueryName()
    {
        JiraProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, method: "POST", queryString: "?type=full&project=FHIR");

        IActionResult result = await c.Ingest("full", null, default);
        await ProxyTestSupport.ExecuteAsync(c, result);

        Assert.Equal("?type=full&project=FHIR", h.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task RebuildIndex_ForwardsPostAndQuery()
    {
        JiraProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, method: "POST", queryString: "?type=cross-refs");

        IActionResult result = await c.RebuildIndex("cross-refs", default);
        await ProxyTestSupport.ExecuteAsync(c, result);

        Assert.Equal(HttpMethod.Post, h.Requests[0].Method);
        Assert.Equal("/api/v1/rebuild-index", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?type=cross-refs", h.Requests[0].RequestUri!.Query);
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
