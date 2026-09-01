using FhirAugury.McpShared.Tools;
using NSubstitute;

namespace FhirAugury.McpShared.Tests;

public class FhirToolsTests
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
    public async Task ListFhirReleases_CallsReleasesEndpointAndFormatsJson()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory("""[{"shortName":"R5"}]""");

        string result = await FhirTools.ListFhirReleases(factory);

        Assert.Equal("/api/v1/fhir/releases", PathAndQuery(handler));
        Assert.Contains("R5", result);
        Assert.Contains("```json", result);
    }

    [Fact]
    public async Task GetFhirStructure_BuildsReleaseScopedPath()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await FhirTools.GetFhirStructure(factory, "Observation", "R5");

        Assert.Equal("/api/v1/fhir/R5/structures/Observation", PathAndQuery(handler));
    }

    [Fact]
    public async Task ListFhirResources_DefaultRelease_WhenReleaseOmitted()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await FhirTools.ListFhirResources(factory);

        Assert.Equal("/api/v1/fhir/default/resources", PathAndQuery(handler));
    }

    [Fact]
    public async Task ListFhirResources_AppendsFilters()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await FhirTools.ListFhirResources(factory, "R5", workGroup: "oo", maturity: 5);

        string pq = PathAndQuery(handler);
        Assert.StartsWith("/api/v1/fhir/R5/resources?", pq);
        Assert.Contains("workGroup=oo", pq);
        Assert.Contains("maturity=5", pq);
    }

    [Fact]
    public async Task LookupFhirCode_EncodesSystemAndPassesCode()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await FhirTools.LookupFhirCode(factory, "http://hl7.org/fhir/observation-status", "final", "R5");

        string pq = PathAndQuery(handler);
        Assert.StartsWith("/api/v1/fhir/R5/codesystems/concept?", pq);
        Assert.Contains("hl7.org", pq);
        Assert.Contains("%2F", pq);          // slashes in the system URL are encoded
        Assert.Contains("code=final", pq);
    }

    [Fact]
    public async Task SearchFhir_IncludesQueryAndTypes()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await FhirTools.SearchFhir(factory, "observation", "R5", "structure,valueset");

        string pq = PathAndQuery(handler);
        Assert.StartsWith("/api/v1/fhir/R5/search?", pq);
        Assert.Contains("q=observation", pq);
        Assert.Contains("types=structure%2Cvalueset", pq);
    }

    [Fact]
    public async Task ExpandFhirValueSet_EncodesUrl()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await FhirTools.ExpandFhirValueSet(factory, "http://hl7.org/fhir/ValueSet/observation-status", "R5");

        string pq = PathAndQuery(handler);
        Assert.StartsWith("/api/v1/fhir/R5/valuesets/concepts?url=", pq);
        Assert.Contains("ValueSet", pq);
    }

    [Fact]
    public async Task ListFhirInterfaces_BuildsReleaseScopedPath()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await FhirTools.ListFhirInterfaces(factory, "R5");

        Assert.Equal("/api/v1/fhir/R5/interfaces", PathAndQuery(handler));
    }

    [Fact]
    public async Task ListFhirInterfaces_AppendsFilters()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await FhirTools.ListFhirInterfaces(factory, "R5", workGroup: "fhir-i", maturity: 5);

        string pq = PathAndQuery(handler);
        Assert.StartsWith("/api/v1/fhir/R5/interfaces?", pq);
        Assert.Contains("workGroup=fhir-i", pq);
        Assert.Contains("maturity=5", pq);
    }

    [Fact]
    public async Task ListFhirElements_NestedAppendsQuery()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await FhirTools.ListFhirElements(factory, "Observation", "R5", nested: true);

        Assert.Equal("/api/v1/fhir/R5/structures/Observation/elements?nested=true", PathAndQuery(handler));
    }

    [Fact]
    public async Task GetFhirCodeSystem_EncodesSystem()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await FhirTools.GetFhirCodeSystem(factory, "http://hl7.org/fhir/observation-status", "R5");

        string pq = PathAndQuery(handler);
        Assert.StartsWith("/api/v1/fhir/R5/codesystems/lookup?system=", pq);
        Assert.Contains("%2F", pq);
    }

    [Fact]
    public async Task ListFhirCodeSystemConcepts_HierarchicalAppends()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await FhirTools.ListFhirCodeSystemConcepts(factory, "x", "R5", hierarchical: true);

        Assert.Equal("/api/v1/fhir/R5/codesystems/concepts?system=x&hierarchical=true", PathAndQuery(handler));
    }

    [Fact]
    public async Task GetFhirValueSet_EncodesUrl()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await FhirTools.GetFhirValueSet(factory, "http://hl7.org/fhir/ValueSet/observation-status", "R5");

        string pq = PathAndQuery(handler);
        Assert.StartsWith("/api/v1/fhir/R5/valuesets/lookup?url=", pq);
        Assert.Contains("ValueSet", pq);
    }

    [Fact]
    public async Task GetFhirOperation_BuildsByCodeRoute()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await FhirTools.GetFhirOperation(factory, "expand", "R5");

        Assert.Equal("/api/v1/fhir/R5/operations/expand", PathAndQuery(handler));
    }

    [Fact]
    public async Task GetFhirSearchParameter_BuildsByCodeRoute()
    {
        (IHttpClientFactory factory, MockHttpHandler handler) = Factory();

        await FhirTools.GetFhirSearchParameter(factory, "Observation-code", "R5");

        Assert.Equal("/api/v1/fhir/R5/searchparameters/Observation-code", PathAndQuery(handler));
    }
}
