using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Controllers;
using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Readers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Fhir.Tests;

public class TerminologyControllerTests : IClassFixture<FhirSpecFixture>
{
    private const string CsUrl = "http://hl7.org/fhir/observation-status";
    private const string VsUrl = "http://hl7.org/fhir/ValueSet/observation-status";
    private readonly FhirSpecFixture _fixture;

    public TerminologyControllerTests(FhirSpecFixture fixture) => _fixture = fixture;

    private (CodeSystemsController Cs, ValueSetsController Vs) Controllers()
    {
        FhirSpecDatabase db = _fixture.CreateDatabase();
        FhirReleaseResolver resolver = new(db);
        FhirSpecReader reader = new(db, resolver);
        return (new CodeSystemsController(resolver, reader), new ValueSetsController(resolver, reader));
    }

    private static T OkBody<T>(IActionResult result)
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<T>(ok.Value);
    }

    [Fact]
    public void CodeSystems_List_EchoesRelease()
    {
        var body = OkBody<FhirReleaseResponse<List<CodeSystemSummary>>>(Controllers().Cs.List("R5"));
        Assert.Equal("R5", body.Release.ShortName);
        Assert.Single(body.Result);
    }

    [Fact]
    public void CodeSystems_Lookup_BySystemUrl_ReturnsDetail()
    {
        var body = OkBody<FhirReleaseResponse<CodeSystemDetail>>(Controllers().Cs.Lookup("R5", CsUrl));
        Assert.Equal("ObservationStatus", body.Result.Summary.Name);
    }

    [Fact]
    public void CodeSystems_Lookup_MissingSystem_BadRequest()
    {
        Assert.IsType<BadRequestObjectResult>(Controllers().Cs.Lookup("R5", null));
    }

    [Fact]
    public void CodeSystems_Lookup_UnknownSystem_NotFound()
    {
        Assert.IsType<NotFoundObjectResult>(Controllers().Cs.Lookup("R5", "http://example/missing"));
    }

    [Fact]
    public void CodeSystems_Concept_BySystemAndCode_ReturnsConcept()
    {
        var body = OkBody<FhirReleaseResponse<ConceptNode>>(Controllers().Cs.Concept("R5", CsUrl, "final"));
        Assert.Equal("final", body.Result.Code);
        Assert.Equal("Final", body.Result.Display);
    }

    [Fact]
    public void CodeSystems_Concept_MissingCode_BadRequest()
    {
        Assert.IsType<BadRequestObjectResult>(Controllers().Cs.Concept("R5", CsUrl, null));
    }

    [Fact]
    public void CodeSystems_Concepts_Hierarchical_ReturnsTree()
    {
        var body = OkBody<FhirReleaseResponse<IReadOnlyList<ConceptNode>>>(
            Controllers().Cs.Concepts("R5", CsUrl, hierarchical: true));
        Assert.Equal(2, body.Result.Count);
    }

    [Fact]
    public void ValueSets_Concepts_Expansion_ReturnsConcepts()
    {
        var body = OkBody<FhirReleaseResponse<IReadOnlyList<ValueSetConceptInfo>>>(
            Controllers().Vs.Concepts("R5", VsUrl));
        Assert.Equal(3, body.Result.Count);
    }

    [Fact]
    public void ValueSets_Bindings_ReturnsReverseBindings()
    {
        var body = OkBody<FhirReleaseResponse<IReadOnlyList<ElementBindingRef>>>(
            Controllers().Vs.Bindings("R5", VsUrl));
        Assert.Equal("Observation.status", Assert.Single(body.Result).Path);
    }

    [Fact]
    public void ValueSets_Lookup_MissingUrl_BadRequest()
    {
        Assert.IsType<BadRequestObjectResult>(Controllers().Vs.Lookup("R5", null));
    }

    [Fact]
    public void ValueSets_Lookup_UnknownUrl_NotFound()
    {
        Assert.IsType<NotFoundObjectResult>(Controllers().Vs.Lookup("R5", "http://example/missing"));
    }
}
