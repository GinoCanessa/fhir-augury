using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Database;
using FhirAugury.Source.Fhir.Readers;

namespace FhirAugury.Source.Fhir.Tests;

public class StructuresReaderTests : IClassFixture<FhirSpecFixture>
{
    private const int R5 = 5;
    private const int R6 = 6;
    private readonly FhirSpecFixture _fixture;

    public StructuresReaderTests(FhirSpecFixture fixture) => _fixture = fixture;

    private FhirSpecReader Reader()
    {
        FhirSpecDatabase db = _fixture.CreateDatabase();
        return new FhirSpecReader(db, new FhirReleaseResolver(db));
    }

    [Fact]
    public void ListStructures_Resources_ReturnsOnlyResources()
    {
        List<StructureSummary> resources = Reader().ListStructures(R5, ["Resource"]);

        Assert.Equal(["Observation", "Patient"], resources.Select(s => s.Name));
        Assert.All(resources, s => Assert.Equal("Resource", s.ArtifactClass));
    }

    [Fact]
    public void ListStructures_DataTypes_ReturnsComplexAndPrimitive()
    {
        List<StructureSummary> datatypes = Reader().ListStructures(R5, ["ComplexType", "PrimitiveType"]);

        Assert.Equal(["HumanName", "string"], datatypes.Select(s => s.Name).OrderBy(n => n));
        Assert.Contains(datatypes, s => s.ArtifactClass == "ComplexType");
        Assert.Contains(datatypes, s => s.ArtifactClass == "PrimitiveType");
    }

    [Fact]
    public void ListStructures_R6_ProfilesAndInterfaces_Partitioned()
    {
        FhirSpecReader reader = Reader();

        Assert.Single(reader.ListStructures(R6, ["Profile"]));
        Assert.Single(reader.ListStructures(R6, ["Interface"]));
        // Profiles and interfaces are excluded from the resources partition.
        Assert.DoesNotContain(reader.ListStructures(R6, ["Resource"]), s => s.ArtifactClass != "Resource");
    }

    [Fact]
    public void ListStructures_WorkGroupFilter_Applies()
    {
        List<StructureSummary> pa = Reader().ListStructures(R5, ["Resource"], workGroup: "pa");

        Assert.Single(pa);
        Assert.Equal("Patient", pa[0].Name);
    }

    [Fact]
    public void GetStructureDetail_Observation_HasNestedElementTree()
    {
        StructureDetail? detail = Reader().GetStructureDetail(R5, "Observation");

        Assert.NotNull(detail);
        Assert.Equal("Observation", detail!.Summary.Name);
        Assert.Equal("normative", detail.Summary.StandardStatus);

        ElementNode root = Assert.Single(detail.Elements);
        Assert.Equal("Observation", root.Path);
        Assert.Equal(["Observation.status", "Observation.code", "Observation.subject"],
            root.Children.Select(c => c.Path));
    }

    [Fact]
    public void GetStructureDetail_StatusElement_HasResolvedBinding()
    {
        StructureDetail detail = Reader().GetStructureDetail(R5, "Observation")!;
        ElementNode status = detail.Elements[0].Children.Single(c => c.Path == "Observation.status");

        Assert.True(status.IsModifier);
        Assert.NotNull(status.Binding);
        Assert.Equal("Required", status.Binding!.Strength);
        Assert.Equal("http://hl7.org/fhir/ValueSet/observation-status", status.Binding.ValueSetUrl);
        Assert.Equal("ObservationStatus", status.Binding.ValueSetName);
    }

    [Fact]
    public void GetStructureDetail_SubjectElement_HasReferenceTargets()
    {
        StructureDetail detail = Reader().GetStructureDetail(R5, "Observation")!;
        ElementNode subject = detail.Elements[0].Children.Single(c => c.Path == "Observation.subject");

        ElementTypeInfo reference = Assert.Single(subject.Types);
        Assert.Equal("Reference", reference.Code);
        Assert.Equal(
            ["http://hl7.org/fhir/StructureDefinition/Patient", "http://hl7.org/fhir/StructureDefinition/Group"],
            reference.TargetProfiles);
    }

    [Fact]
    public void GetStructureDetail_CodeElement_HasAdditionalBinding()
    {
        StructureDetail detail = Reader().GetStructureDetail(R5, "Observation")!;
        ElementNode code = detail.Elements[0].Children.Single(c => c.Path == "Observation.code");

        Assert.NotNull(code.Binding);
        AdditionalBindingInfo additional = Assert.Single(code.Binding!.AdditionalBindings);
        Assert.Equal("preferred", additional.Purpose);
    }

    [Fact]
    public void GetElements_Flat_ReturnsAllElementsWithoutNesting()
    {
        IReadOnlyList<ElementNode>? flat = Reader().GetElements(R5, "Observation", nested: false);

        Assert.NotNull(flat);
        Assert.Equal(4, flat!.Count); // root + 3 children
        Assert.All(flat, e => Assert.Empty(e.Children));
    }

    [Fact]
    public void GetElement_ByDottedPath_ReturnsSingleElement()
    {
        ElementNode? element = Reader().GetElement(R5, "Patient", "Patient.contact.name");

        Assert.NotNull(element);
        Assert.Equal("Patient.contact.name", element!.Path);
        Assert.Equal("HumanName", Assert.Single(element.Types).Code);
    }

    [Fact]
    public void GetElement_UnknownPath_ReturnsNull()
    {
        Assert.Null(Reader().GetElement(R5, "Observation", "Observation.bogus"));
    }

    [Fact]
    public void GetStructureDetail_UnknownStructure_ReturnsNull()
    {
        Assert.Null(Reader().GetStructureDetail(R5, "DoesNotExist"));
    }
}
