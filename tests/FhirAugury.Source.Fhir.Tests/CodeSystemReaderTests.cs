using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Readers;

namespace FhirAugury.Source.Fhir.Tests;

public class CodeSystemReaderTests : IClassFixture<FhirSpecFixture>
{
    private const int R5 = 5;
    private const string CsUrl = "http://hl7.org/fhir/observation-status";
    private readonly FhirSpecFixture _fixture;

    public CodeSystemReaderTests(FhirSpecFixture fixture) => _fixture = fixture;

    private FhirSpecReader Reader()
    {
        FhirSpecDatabase db = _fixture.CreateDatabase();
        return new FhirSpecReader(db, new FhirReleaseResolver(db));
    }

    [Fact]
    public void ListCodeSystems_ReturnsSeeded()
    {
        CodeSystemSummary cs = Assert.Single(Reader().ListCodeSystems(R5));
        Assert.Equal("ObservationStatus", cs.Name);
        Assert.Equal("complete", cs.Content);
    }

    [Theory]
    [InlineData(CsUrl)]
    [InlineData("observation-status")]      // by id
    [InlineData("ObservationStatus")]       // by name
    public void GetCodeSystem_ResolvesByIdUrlOrName(string idOrUrl)
    {
        CodeSystemDetail? detail = Reader().GetCodeSystem(R5, idOrUrl);

        Assert.NotNull(detail);
        Assert.Equal(3, detail!.ConceptCount);
        Assert.True(detail.HasHierarchy);
        Assert.Contains(detail.PropertyDefinitions, p => p.Code == "status" && p.Type == "code");
    }

    [Fact]
    public void GetConcepts_Hierarchical_NestsChildren()
    {
        IReadOnlyList<ConceptNode>? concepts = Reader().GetConcepts(R5, CsUrl, hierarchical: true);

        Assert.NotNull(concepts);
        // Roots are 'final' (with child 'amended') and 'registered'.
        Assert.Equal(["final", "registered"], concepts!.Select(c => c.Code));
        ConceptNode final = concepts[0];
        Assert.Equal("amended", Assert.Single(final.Children).Code);
    }

    [Fact]
    public void GetConcepts_Flat_AllConceptsNoNesting()
    {
        IReadOnlyList<ConceptNode>? concepts = Reader().GetConcepts(R5, CsUrl, hierarchical: false);

        Assert.NotNull(concepts);
        Assert.Equal(3, concepts!.Count);
        Assert.All(concepts, c => Assert.Empty(c.Children));
    }

    [Fact]
    public void GetConcept_ReturnsDisplayDefinitionDesignationsAndProperties()
    {
        ConceptNode? final = Reader().GetConcept(R5, CsUrl, "final");

        Assert.NotNull(final);
        Assert.Equal("Final", final!.Display);
        Assert.Equal("The observation is complete.", final.Definition);

        ConceptDesignation designation = Assert.Single(final.Designations);
        Assert.Equal("label", designation.Use);
        Assert.Equal("Final result", designation.Value);

        ConceptProperty property = Assert.Single(final.Properties);
        Assert.Equal("status", property.Code);
        Assert.Equal("active", property.Value);
    }

    [Fact]
    public void GetConcept_UnknownCode_ReturnsNull()
    {
        Assert.Null(Reader().GetConcept(R5, CsUrl, "no-such-code"));
    }

    [Fact]
    public void GetCodeSystem_Unknown_ReturnsNull()
    {
        Assert.Null(Reader().GetCodeSystem(R5, "http://example/missing"));
    }
}
