using System.Net;
using FhirAugury.Orchestrator.Controllers.Proxies;
using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Tests.Proxies;

public class GitHubProxyControllerTests
{
    private static GitHubProxyController NewController(out ProxyTestSupport.CapturingHandler handler,
        string responseBody = """{"ok":true}""")
    {
        (SourceHttpClient client, ProxyTestSupport.CapturingHandler h) =
            ProxyTestSupport.CreateClient("github", responseBody);
        handler = h;
        return new GitHubProxyController(client);
    }

    public static IEnumerable<object[]> ItemSubResources =>
    [
        ["GetItem", "/api/v1/items/owner/name%23123"],
        ["GetRelated", "/api/v1/items/related/owner/name%23123"],
        ["GetSnapshot", "/api/v1/items/snapshot/owner/name%23123"],
        ["GetContent", "/api/v1/items/content/owner/name%23123"],
        ["GetComments", "/api/v1/items/comments/owner/name%23123"],
        ["GetCommits", "/api/v1/items/commits/owner/name%23123"],
        ["GetPullRequest", "/api/v1/items/pr/owner/name%23123"],
        ["GetTicketsForPullRequest", "/api/v1/items/pr-tickets/owner/name%23123"],
    ];

    [Theory]
    [MemberData(nameof(ItemSubResources))]
    public async Task ItemActionFirst_PreservesKey(string action, string expectedPath)
    {
        GitHubProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);

        const string key = "owner/name#123";
        IActionResult r = action switch
        {
            "GetItem" => await c.GetItem(key, null, null, default),
            "GetRelated" => await c.GetRelated(key, null, null, default),
            "GetSnapshot" => await c.GetSnapshot(key, null, null, default),
            "GetContent" => await c.GetContent(key, null, default),
            "GetComments" => await c.GetComments(key, default),
            "GetCommits" => await c.GetCommits(key, default),
            "GetPullRequest" => await c.GetPullRequest(key, default),
            "GetTicketsForPullRequest" => await c.GetTicketsForPullRequest(key, default),
            _ => throw new InvalidOperationException(),
        };
        await ProxyTestSupport.ExecuteAsync(c, r);

        Assert.Single(h.Requests);
        Assert.Equal(expectedPath, h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetPullRequestsForTicket_ForwardsJiraKey()
    {
        GitHubProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);

        IActionResult result = await c.GetPullRequestsForTicket("FHIR-123", default);
        await ProxyTestSupport.ExecuteAsync(c, result);

        Assert.Equal("/api/v1/items/ticket-prs/FHIR-123", h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ContentReferredBy_PreservesSourceSpecificQuery()
    {
        GitHubProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, queryString: "?value=FHIR-123&sourceType=jira");

        IActionResult result = await c.ContentReferredBy("FHIR-123", "jira", null, null, default);
        await ProxyTestSupport.ExecuteAsync(c, result);

        Assert.Equal("/api/v1/content/referred-by", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?value=FHIR-123&sourceType=jira", h.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task ContentRelatedByKeyword_PreservesGitHubKey()
    {
        GitHubProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);

        IActionResult result = await c.ContentRelatedByKeyword(
            "github", "owner/name#123", null, null, null, default);
        await ProxyTestSupport.ExecuteAsync(c, result);

        Assert.Equal(
            "/api/v1/content/related-by-keyword/github/owner/name%23123",
            h.Requests[0].RequestUri!.AbsolutePath);
    }

    public static IEnumerable<object[]> JiraSpecsCases =>
    [
        ["ListJiraSpecs", "/api/v1/jira-specs"],
        ["ListJiraSpecWorkgroups", "/api/v1/jira-specs/workgroups"],
        ["ListJiraSpecFamilies", "/api/v1/jira-specs/families"],
        ["JiraSpecByGitUrl", "/api/v1/jira-specs/by-git-url"],
        ["JiraSpecByCanonical", "/api/v1/jira-specs/by-canonical"],
    ];

    [Theory]
    [MemberData(nameof(JiraSpecsCases))]
    public async Task JiraSpecsListings_ForwardCorrectPath(string action, string expectedPath)
    {
        GitHubProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);
        IActionResult r = action switch
        {
            "ListJiraSpecs" => await c.ListJiraSpecs(null, null, default),
            "ListJiraSpecWorkgroups" => await c.ListJiraSpecWorkgroups(default),
            "ListJiraSpecFamilies" => await c.ListJiraSpecFamilies(default),
            "JiraSpecByGitUrl" => await c.JiraSpecByGitUrl(null, default),
            "JiraSpecByCanonical" => await c.JiraSpecByCanonical(null, default),
            _ => throw new InvalidOperationException(),
        };
        await ProxyTestSupport.ExecuteAsync(c, r);
        Assert.Equal(expectedPath, h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetJiraSpec_KeyEncoded()
    {
        GitHubProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);
        IActionResult r = await c.GetJiraSpec("FHIR/r5", default);
        await ProxyTestSupport.ExecuteAsync(c, r);
        Assert.Equal("/api/v1/jira-specs/FHIR%2Fr5", h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ListRepoTags_ForwardsTwoSegmentRoute()
    {
        GitHubProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);
        IActionResult r = await c.ListRepoTags("HL7", "fhir-core", default);
        await ProxyTestSupport.ExecuteAsync(c, r);
        Assert.Equal("/api/v1/repos/HL7/fhir-core/tags", h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task SearchRepoTags_PreservesQueryString()
    {
        GitHubProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, queryString: "?tag=v5.0.0");
        IActionResult r = await c.SearchRepoTags("HL7", "fhir-core", default);
        await ProxyTestSupport.ExecuteAsync(c, r);
        Assert.Equal("?tag=v5.0.0", h.Requests[0].RequestUri!.Query);
    }

    [Theory]
    [InlineData("trigger", "/api/v1/ingest/trigger")]
    [InlineData("rebuild-index", "/api/v1/rebuild-index")]
    public async Task MissingIngestionRoutes_ForwardPost(string action, string expectedPath)
    {
        GitHubProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
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
