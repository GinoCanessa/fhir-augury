using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Controllers;
using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Readers;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Source.Fhir.Tests;

public class StructuresControllerTests : IClassFixture<FhirSpecFixture>
{
    private readonly FhirSpecFixture _fixture;

    public StructuresControllerTests(FhirSpecFixture fixture) => _fixture = fixture;

    private StructuresController Controller()
    {
        FhirSpecDatabase db = _fixture.CreateDatabase();
        FhirReleaseResolver resolver = new(db);
        return new StructuresController(resolver, new FhirSpecReader(db, resolver));
    }

    private static T OkBody<T>(IActionResult result)
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<T>(ok.Value);
    }

    [Fact]
    public void GetResources_EchoesResolvedReleaseAndLists()
    {
        IActionResult result = Controller().GetResources("R5");

        var body = OkBody<FhirReleaseResponse<List<StructureSummary>>>(result);
        Assert.Equal("R5", body.Release.ShortName);
        Assert.Equal(2, body.Result.Count);
    }

    [Fact]
    public void GetResources_BlankRelease_ResolvesDefaultStable()
    {
        IActionResult result = Controller().GetResources("");

        var body = OkBody<FhirReleaseResponse<List<StructureSummary>>>(result);
        Assert.Equal("R5", body.Release.ShortName);
    }

    [Fact]
    public void GetDataTypes_ReturnsComplexAndPrimitive()
    {
        var body = OkBody<FhirReleaseResponse<List<StructureSummary>>>(Controller().GetDataTypes("R5"));
        Assert.Equal(2, body.Result.Count);
    }

    [Fact]
    public void GetProfiles_R6_ReturnsProfiles()
    {
        var body = OkBody<FhirReleaseResponse<List<StructureSummary>>>(Controller().GetProfiles("R6"));
        Assert.Single(body.Result);
        Assert.Equal("Profile", body.Result[0].ArtifactClass);
    }

    [Fact]
    public void GetInterfaces_R6_ReturnsInterfaces()
    {
        var body = OkBody<FhirReleaseResponse<List<StructureSummary>>>(Controller().GetInterfaces("R6"));
        Assert.Single(body.Result);
        Assert.Equal("Interface", body.Result[0].ArtifactClass);
    }

    [Fact]
    public void GetStructure_Known_ReturnsDetail()
    {
        var body = OkBody<FhirReleaseResponse<StructureDetail>>(Controller().GetStructure("R5", "Observation"));
        Assert.Equal("Observation", body.Result.Summary.Name);
        Assert.NotEmpty(body.Result.Elements);
    }

    [Fact]
    public void GetStructure_UnknownStructure_NotFound()
    {
        Assert.IsType<NotFoundObjectResult>(Controller().GetStructure("R5", "Nope"));
    }

    [Fact]
    public void GetStructure_UnknownRelease_NotFound()
    {
        Assert.IsType<NotFoundObjectResult>(Controller().GetStructure("R99", "Observation"));
    }

    [Fact]
    public void GetElement_ByPath_ReturnsElement()
    {
        var body = OkBody<FhirReleaseResponse<ElementNode>>(
            Controller().GetElement("R5", "Patient", "Patient.contact.name"));
        Assert.Equal("Patient.contact.name", body.Result.Path);
    }

    [Fact]
    public void GetElements_Nested_ReturnsTree()
    {
        var body = OkBody<FhirReleaseResponse<IReadOnlyList<ElementNode>>>(
            Controller().GetElements("R5", "Observation", nested: true));
        ElementNode root = Assert.Single(body.Result);
        Assert.Equal(3, root.Children.Count);
    }
}
