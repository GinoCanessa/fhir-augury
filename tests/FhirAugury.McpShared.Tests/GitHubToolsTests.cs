using FhirAugury.McpShared.Tools;
using NSubstitute;

namespace FhirAugury.McpShared.Tests;

public class GitHubToolsTests
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
    public async Task GetGitHubRepo_BuildsOwnerNameRoute()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await GitHubReposTools.GetGitHubRepo(factory, "HL7", "fhir");

        Assert.Equal("/api/v1/github/repos/HL7/fhir", PathAndQuery(handler));
    }

    [Fact]
    public async Task ListGitHubWorkGroups_HitsBaseRoute()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await WorkGroupTools.ListGitHubWorkGroups(factory);

        Assert.Equal("/api/v1/github/workgroups", PathAndQuery(handler));
    }

    [Fact]
    public async Task ListGitHubWorkGroupFiles_EscapesRepoAndWorkgroup()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await WorkGroupTools.ListGitHubWorkGroupFiles(factory, "HL7/fhir", "fhir-i");

        Assert.Equal("/api/v1/github/workgroups/files?repo=HL7%2Ffhir&workgroup=fhir-i", PathAndQuery(handler));
    }

    [Fact]
    public async Task ListGitHubWorkGroupArtifacts_AppendsPaging()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await WorkGroupTools.ListGitHubWorkGroupArtifacts(factory, "HL7/fhir", "fhir-i", limit: 5, offset: 10);

        string pq = PathAndQuery(handler);
        Assert.StartsWith("/api/v1/github/workgroups/artifacts?repo=HL7%2Ffhir&workgroup=fhir-i", pq);
        Assert.Contains("limit=5", pq);
        Assert.Contains("offset=10", pq);
    }

    [Fact]
    public async Task ListGitHubWorkGroupUnresolved_NoRepo_HitsBaseRoute()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await WorkGroupTools.ListGitHubWorkGroupUnresolved(factory);

        Assert.Equal("/api/v1/github/workgroups/unresolved", PathAndQuery(handler));
    }

    [Fact]
    public async Task ListGitHubWorkGroupUnresolved_WithRepo_AppendsFilter()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await WorkGroupTools.ListGitHubWorkGroupUnresolved(factory, "HL7/fhir");

        Assert.Equal("/api/v1/github/workgroups/unresolved?repo=HL7%2Ffhir", PathAndQuery(handler));
    }
}
