using FhirAugury.Orchestrator.Controllers.Proxies;
using FhirAugury.Orchestrator.Routing;

namespace FhirAugury.Orchestrator.Tests.Proxies;

public class FhirProxyControllerTests
{
    private static FhirProxyController NewController(out ProxyTestSupport.CapturingHandler handler,
        string responseBody = """{"ok":true}""")
    {
        (SourceHttpClient client, ProxyTestSupport.CapturingHandler h) =
            ProxyTestSupport.CreateClient("fhir", responseBody);
        handler = h;
        return new FhirProxyController(client);
    }

    [Fact]
    public async Task Releases_ForwardsToReleases()
    {
        FhirProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);

        await ProxyTestSupport.ExecuteAsync(c, await c.Releases(default));

        Assert.Single(h.Requests);
        Assert.Equal("/api/v1/releases", h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Resources_BuildsReleaseScopedPath()
    {
        FhirProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);

        await ProxyTestSupport.ExecuteAsync(c, await c.Resources("R5", null, null, null, null, default));

        Assert.Equal("/api/v1/R5/resources", h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Structure_ForwardsReleaseAndName()
    {
        FhirProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);

        await ProxyTestSupport.ExecuteAsync(c, await c.Structure("R5", "Observation", default));

        Assert.Equal("/api/v1/R5/structures/Observation", h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Element_CatchAll_ForwardsDottedPath()
    {
        FhirProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c);

        await ProxyTestSupport.ExecuteAsync(c, await c.Element("R5", "Patient", "Patient.contact.name", default));

        Assert.Equal("/api/v1/R5/structures/Patient/elements/Patient.contact.name",
            h.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task CodeSystemConcept_PreservesQueryString()
    {
        FhirProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, queryString: "?system=http://hl7.org/fhir/observation-status&code=final");

        await ProxyTestSupport.ExecuteAsync(c, await c.CodeSystemConcept("R5", null, null, default));

        Assert.Equal("/api/v1/R5/codesystems/concept", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("code=final", h.Requests[0].RequestUri!.Query);
        Assert.Contains("observation-status", h.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task Search_PreservesQueryString()
    {
        FhirProxyController c = NewController(out ProxyTestSupport.CapturingHandler h);
        ProxyTestSupport.SetRequest(c, queryString: "?q=observation&types=structure,valueset");

        await ProxyTestSupport.ExecuteAsync(c, await c.Search("R5", null, null, null, default));

        Assert.Equal("/api/v1/R5/search", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("q=observation", h.Requests[0].RequestUri!.Query);
    }
}
