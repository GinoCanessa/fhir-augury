using FhirAugury.McpShared.Tools;
using NSubstitute;

namespace FhirAugury.McpShared.Tests;

public class JiraReadModelToolsTests
{
    private static (IHttpClientFactory Factory, MockHttpHandler Handler) Factory(string json = "{}")
    {
        MockHttpHandler handler = new(json);
        HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost") };
        IHttpClientFactory factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("orchestrator").Returns(client);
        return (factory, handler);
    }

    private static string PathAndQuery(MockHttpHandler handler)
        => handler.LastRequest!.RequestUri!.PathAndQuery;

    [Fact]
    public async Task ListJiraBalDef_NoFilters_HitsBaseRoute()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await JiraBalDefTools.ListJiraBalDef(factory);

        Assert.Equal("/api/v1/jira/baldef", PathAndQuery(handler));
    }

    [Fact]
    public async Task ListJiraBalDef_AppendsFilters()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await JiraBalDefTools.ListJiraBalDef(factory, cycle: "2025-Sep", level: "Normative");

        string pq = PathAndQuery(handler);
        Assert.StartsWith("/api/v1/jira/baldef?", pq);
        Assert.Contains("cycle=2025-Sep", pq);
        Assert.Contains("level=Normative", pq);
    }

    [Fact]
    public async Task GetJiraBalDef_BuildsByKeyRoute()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await JiraBalDefTools.GetJiraBalDef(factory, "BALDEF-1");

        Assert.Equal("/api/v1/jira/baldef/BALDEF-1", PathAndQuery(handler));
    }

    [Fact]
    public async Task ListJiraBallot_AppendsCycle()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await JiraBallotTools.ListJiraBallot(factory, cycle: "2025-Sep");

        Assert.Equal("/api/v1/jira/ballot?cycle=2025-Sep", PathAndQuery(handler));
    }

    [Fact]
    public async Task GetJiraBallot_BuildsByKeyRoute()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await JiraBallotTools.GetJiraBallot(factory, "BALLOT-1");

        Assert.Equal("/api/v1/jira/ballot/BALLOT-1", PathAndQuery(handler));
    }

    [Fact]
    public async Task ListJiraPss_AppendsWorkGroupAndStatus()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await JiraPssTools.ListJiraPss(factory, workGroup: "fhir-i", status: "Approved");

        string pq = PathAndQuery(handler);
        Assert.StartsWith("/api/v1/jira/pss?", pq);
        Assert.Contains("workGroup=fhir-i", pq);
        Assert.Contains("status=Approved", pq);
    }

    [Fact]
    public async Task GetJiraPss_BuildsByKeyRoute()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await JiraPssTools.GetJiraPss(factory, "PSS-1");

        Assert.Equal("/api/v1/jira/pss/PSS-1", PathAndQuery(handler));
    }
}
