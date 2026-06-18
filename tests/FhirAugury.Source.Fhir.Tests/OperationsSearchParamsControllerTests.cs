using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Controllers;
using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Readers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Fhir.Tests;

public class OperationsSearchParamsControllerTests : IClassFixture<FhirSpecFixture>
{
    private readonly FhirSpecFixture _fixture;

    public OperationsSearchParamsControllerTests(FhirSpecFixture fixture) => _fixture = fixture;

    private (OperationsController Ops, SearchParametersController Sp, ResolveController Resolve) Controllers()
    {
        FhirSpecDatabase db = _fixture.CreateDatabase();
        FhirReleaseResolver resolver = new(db);
        FhirSpecReader reader = new(db, resolver);
        return (
            new OperationsController(resolver, reader),
            new SearchParametersController(resolver, reader),
            new ResolveController(resolver, reader));
    }

    private static T OkBody<T>(IActionResult result)
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<T>(ok.Value);
    }

    [Fact]
    public void Operations_List_EchoesRelease()
    {
        var body = OkBody<FhirReleaseResponse<List<OperationSummary>>>(Controllers().Ops.List("R5"));
        Assert.Equal("R5", body.Release.ShortName);
        Assert.Single(body.Result);
    }

    [Fact]
    public void Operations_Get_ReturnsDetail()
    {
        var body = OkBody<FhirReleaseResponse<OperationDetail>>(Controllers().Ops.Get("R5", "expand"));
        Assert.Equal(2, body.Result.Parameters.Count);
    }

    [Fact]
    public void Operations_Get_Unknown_NotFound()
    {
        Assert.IsType<NotFoundObjectResult>(Controllers().Ops.Get("R5", "nope"));
    }

    [Fact]
    public void SearchParameters_List_BaseFilter()
    {
        var body = OkBody<FhirReleaseResponse<List<SearchParameterInfo>>>(
            Controllers().Sp.List("R5", baseResource: "Observation"));
        Assert.Equal(4, body.Result.Count);
    }

    [Fact]
    public void SearchParameters_Get_ReturnsParameter()
    {
        var body = OkBody<FhirReleaseResponse<SearchParameterInfo>>(
            Controllers().Sp.Get("R5", "Observation-code"));
        Assert.Equal("code", body.Result.Code);
    }

    [Fact]
    public void Resolve_ByUrl_ReturnsArtifact()
    {
        var body = OkBody<FhirReleaseResponse<ResolveResult>>(
            Controllers().Resolve.Resolve("R5", "http://hl7.org/fhir/StructureDefinition/Observation"));
        Assert.Equal("Resource", body.Result.Kind);
    }

    [Fact]
    public void Resolve_MissingUrl_BadRequest()
    {
        Assert.IsType<BadRequestObjectResult>(Controllers().Resolve.Resolve("R5", null));
    }

    [Fact]
    public void Resolve_Unknown_NotFound()
    {
        Assert.IsType<NotFoundObjectResult>(
            Controllers().Resolve.Resolve("R5", "http://example.org/missing"));
    }
}
