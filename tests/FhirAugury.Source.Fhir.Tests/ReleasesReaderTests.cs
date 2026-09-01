using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Readers;

namespace FhirAugury.Source.Fhir.Tests;

public class ReleasesReaderTests : IClassFixture<FhirSpecFixture>
{
    private readonly FhirSpecFixture _fixture;

    public ReleasesReaderTests(FhirSpecFixture fixture) => _fixture = fixture;

    private FhirSpecReader Reader()
    {
        FhirSpecDatabase db = _fixture.CreateDatabase();
        return new FhirSpecReader(db, new FhirReleaseResolver(db));
    }

    [Fact]
    public void ListReleases_ReturnsAllPackagesOrderedByKey()
    {
        List<ReleaseInfo> releases = Reader().ListReleases();

        Assert.Equal(4, releases.Count);
        Assert.Equal(["DSTU2", "R4", "R5", "R6"], releases.Select(r => r.ShortName));
        Assert.Equal("hl7.fhir.r5.core", releases.Single(r => r.ShortName == "R5").PackageId);
    }

    [Fact]
    public void GetCounts_ReflectsSeededArtifacts()
    {
        FhirSpecCounts counts = Reader().GetCounts();

        Assert.Equal(4, counts.Releases);
        Assert.Equal(7, counts.Structures);   // 4 R5 + 3 R6
        Assert.Equal(1, counts.CodeSystems);
        Assert.Equal(1, counts.ValueSets);
        Assert.Equal(1, counts.Operations);
        Assert.Equal(4, counts.SearchParameters);
    }
}
