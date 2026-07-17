using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Readers;

namespace FhirAugury.Source.Fhir.Tests;

public class SearchParameterReaderTests : IClassFixture<FhirSpecFixture>
{
    private const int R5 = 5;
    private readonly FhirSpecFixture _fixture;

    public SearchParameterReaderTests(FhirSpecFixture fixture) => _fixture = fixture;

    private FhirSpecReader Reader()
    {
        FhirSpecDatabase db = _fixture.CreateDatabase();
        return new FhirSpecReader(db, new FhirReleaseResolver(db));
    }

    [Fact]
    public void ListSearchParameters_NoFilter_ReturnsAll()
    {
        Assert.Equal(4, Reader().ListSearchParameters(R5).Count);
    }

    [Fact]
    public void ListSearchParameters_BaseFilter_MatchesWholeToken()
    {
        // 'Condition' only appears in the multi-base 'patient' parameter.
        SearchParameterInfo sp = Assert.Single(Reader().ListSearchParameters(R5, baseResource: "Condition"));
        Assert.Equal("patient", sp.Code);
        Assert.Contains("Observation", sp.Base);
        Assert.Contains("Condition", sp.Base);
    }

    [Fact]
    public void ListSearchParameters_CodeFilter_Applies()
    {
        SearchParameterInfo sp = Assert.Single(Reader().ListSearchParameters(R5, code: "subject"));
        Assert.Equal("subject", sp.Code);
        Assert.Equal(["Patient", "Group"], sp.Targets);
    }

    [Theory]
    [InlineData("Observation-code")]   // by id
    [InlineData("code")]               // by code
    public void GetSearchParameter_ResolvesByIdOrCode(string idOrCode)
    {
        SearchParameterInfo? sp = Reader().GetSearchParameter(R5, idOrCode);

        Assert.NotNull(sp);
        Assert.Equal("code", sp!.Code);
        Assert.Equal("token", sp.Type);
        Assert.Equal("Observation.code", sp.Expression);
        Assert.Equal(["Observation"], sp.Base);
    }

    [Fact]
    public void GetSearchParameter_Composite_HasComponents()
    {
        SearchParameterInfo? sp = Reader().GetSearchParameter(R5, "Observation-combo");

        Assert.NotNull(sp);
        Assert.Equal(2, sp!.Components.Count);
        Assert.Equal("code", sp.Components[0].Expression);
    }

    [Fact]
    public void GetSearchParameter_Unknown_ReturnsNull()
    {
        Assert.Null(Reader().GetSearchParameter(R5, "no-such-sp"));
    }
}
