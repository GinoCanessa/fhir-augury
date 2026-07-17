using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Readers;

namespace FhirAugury.Source.Fhir.Tests;

public class OperationReaderTests : IClassFixture<FhirSpecFixture>
{
    private const int R5 = 5;
    private readonly FhirSpecFixture _fixture;

    public OperationReaderTests(FhirSpecFixture fixture) => _fixture = fixture;

    private FhirSpecReader Reader()
    {
        FhirSpecDatabase db = _fixture.CreateDatabase();
        return new FhirSpecReader(db, new FhirReleaseResolver(db));
    }

    [Fact]
    public void ListOperations_ReturnsSeeded()
    {
        OperationSummary op = Assert.Single(Reader().ListOperations(R5));
        Assert.Equal("expand", op.Code);
        Assert.Equal("Operation", op.Kind);
        Assert.Equal(["ValueSet"], op.ResourceTypes);
        Assert.True(op.Type);
    }

    [Theory]
    [InlineData("expand")]            // by code
    [InlineData("ValueSet-expand")]   // by id
    [InlineData("Expand")]            // by name
    public void GetOperation_ResolvesAndReturnsParameters(string idOrCode)
    {
        OperationDetail? detail = Reader().GetOperation(R5, idOrCode);

        Assert.NotNull(detail);
        Assert.Equal("expand", detail!.Summary.Code);
        Assert.Equal(["url", "return"], detail.Parameters.Select(p => p.Name));

        OperationParameterInfo url = detail.Parameters[0];
        Assert.Equal("in", url.Use);
        Assert.Equal("uri", url.Type);

        OperationParameterInfo ret = detail.Parameters[1];
        Assert.Equal("out", ret.Use);
        Assert.Equal("ValueSet", ret.Type);
    }

    [Fact]
    public void GetOperation_Unknown_ReturnsNull()
    {
        Assert.Null(Reader().GetOperation(R5, "no-such-op"));
    }
}
