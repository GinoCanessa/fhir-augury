using FhirAugury.Source.GitHub.Ingestion.Parsing;

namespace FhirAugury.Source.GitHub.Tests.Ingestion.Parsing;

public class FhirIniParserTests
{
    private readonly FhirIniParser _parser = new();

    [Fact]
    public void Parse_TypesSection_BareNames()
    {
        string[] lines =
        [
            "[types]",
            "Annotation",
            "CodeableConcept",
        ];

        List<ArtifactEntry> result = _parser.ParseLines(lines);

        Assert.Contains(result, e => e.Name == "Annotation" && e.Category == "type" && e.DirectoryKey == "annotation");
        Assert.Contains(result, e => e.Name == "CodeableConcept" && e.Category == "type" && e.DirectoryKey == "codeableconcept");
    }

    [Fact]
    public void Parse_TypesSection_KeyValuePairs()
    {
        string[] lines =
        [
            "[types]",
            "SimpleQuantity=Quantity",
            "MoneyQuantity=Quantity",
        ];

        List<ArtifactEntry> result = _parser.ParseLines(lines);

        // Key (left side) is the type name
        Assert.Contains(result, e => e.Name == "SimpleQuantity" && e.Category == "type" && e.DirectoryKey == "simplequantity");
        Assert.Contains(result, e => e.Name == "MoneyQuantity" && e.Category == "type" && e.DirectoryKey == "moneyquantity");
    }

    [Fact]
    public void Parse_TypesSection_CommentsIgnored()
    {
        string[] lines =
        [
            "[types]",
            "Annotation",
            ";Population",
            "CodeableConcept",
        ];

        List<ArtifactEntry> result = _parser.ParseLines(lines);

        Assert.DoesNotContain(result, e => e.Name == "Population");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Parse_InfrastructureSection()
    {
        string[] lines =
        [
            "[infrastructure]",
            "Base=abstract",
            "Element=abstract",
            "Extension",
        ];

        List<ArtifactEntry> result = _parser.ParseLines(lines);

        Assert.Contains(result, e => e.Name == "Base" && e.Category == "infrastructure" && e.DirectoryKey == "base");
        Assert.Contains(result, e => e.Name == "Extension" && e.Category == "infrastructure");
        // Infrastructure items don't carry modifier from INI
        Assert.All(result, e => Assert.Empty(e.Modifiers));
    }

    [Fact]
    public void Parse_ResourcesSection()
    {
        string[] lines =
        [
            "[resources]",
            "patient=Patient",
            "allergyintolerance=AllergyIntolerance",
        ];

        List<ArtifactEntry> result = _parser.ParseLines(lines);

        Assert.Contains(result, e => e.Name == "Patient" && e.Category == "resource" && e.DirectoryKey == "patient");
        Assert.Contains(result, e => e.Name == "AllergyIntolerance" && e.Category == "resource" && e.DirectoryKey == "allergyintolerance");
    }

    [Fact]
    public void Parse_DraftResources_MergesModifier()
    {
        string[] lines =
        [
            "[resources]",
            "adverseevent=AdverseEvent",
            "[draft-resources]",
            "AdverseEvent=1",
        ];

        List<ArtifactEntry> result = _parser.ParseLines(lines);

        ArtifactEntry entry = Assert.Single(result, e => e.Name == "AdverseEvent");
        Assert.Equal("resource", entry.Category);
        Assert.Contains("draft", entry.Modifiers);
    }

    [Fact]
    public void Parse_DraftResources_StandaloneCreatesEntry()
    {
        string[] lines =
        [
            "[draft-resources]",
            "NewResource=1",
        ];

        List<ArtifactEntry> result = _parser.ParseLines(lines);

        ArtifactEntry entry = Assert.Single(result, e => e.Name == "NewResource");
        Assert.Equal("resource", entry.Category);
        Assert.Contains("draft", entry.Modifiers);
    }

    [Fact]
    public void Parse_LogicalSection()
    {
        string[] lines =
        [
            "[logical]",
            "definition",
            "fivews",
        ];

        List<ArtifactEntry> result = _parser.ParseLines(lines);

        Assert.Contains(result, e => e.Name == "definition" && e.Category == "logical-model" && e.DirectoryKey == "definition");
        Assert.Contains(result, e => e.Name == "fivews" && e.Category == "logical-model");
    }

    [Fact]
    public void Parse_RemovedResources_AddsModifier()
    {
        string[] lines =
        [
            "[removed-resources]",
            "Animal",
            "BodySite",
        ];

        List<ArtifactEntry> result = _parser.ParseLines(lines);

        Assert.All(result, e =>
        {
            Assert.Equal("resource", e.Category);
            Assert.Contains("removed", e.Modifiers);
        });
    }

    [Fact]
    public void Parse_RemovedResources_MergesWithExisting()
    {
        string[] lines =
        [
            "[resources]",
            "animal=Animal",
            "[removed-resources]",
            "Animal",
        ];

        List<ArtifactEntry> result = _parser.ParseLines(lines);

        ArtifactEntry entry = Assert.Single(result, e => e.Name == "Animal");
        Assert.Equal("resource", entry.Category);
        Assert.Contains("removed", entry.Modifiers);
    }

    [Fact]
    public void Parse_ResourceInfrastructure_ProducesResourceCategory()
    {
        string[] lines =
        [
            "[resource-infrastructure]",
            "resource=abstract,Resource",
            "domainresource=abstract,DomainResource",
            "parameters=concrete,Parameters",
        ];

        List<ArtifactEntry> result = _parser.ParseLines(lines);

        Assert.Contains(result, e => e.Name == "Resource" && e.Category == "resource" && e.DirectoryKey == "resource");
        Assert.Contains(result, e => e.Name == "DomainResource" && e.Category == "resource" && e.DirectoryKey == "domainresource");
        Assert.Contains(result, e => e.Name == "Parameters" && e.Category == "resource" && e.DirectoryKey == "parameters");
    }

    [Fact]
    public void Parse_EmptySections_ReturnsEmpty()
    {
        string[] lines = [];

        List<ArtifactEntry> result = _parser.ParseLines(lines);

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_UnknownSections_Ignored()
    {
        string[] lines =
        [
            "[type-pages]",
            "narrative=narrative",
            "[custom-section]",
            "something=else",
        ];

        List<ArtifactEntry> result = _parser.ParseLines(lines);

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_InfrastructureOverridesType()
    {
        // If an item is in both [types] and [infrastructure], infrastructure wins
        string[] lines =
        [
            "[types]",
            "Extension",
            "[infrastructure]",
            "Extension",
        ];

        List<ArtifactEntry> result = _parser.ParseLines(lines);

        ArtifactEntry entry = Assert.Single(result, e => e.Name == "Extension");
        Assert.Equal("infrastructure", entry.Category);
    }

    [Fact]
    public void Parse_FullIniSample()
    {
        string[] lines =
        [
            "[types]",
            "Annotation",
            "CodeableConcept",
            "SimpleQuantity=Quantity",
            ";Population",
            "",
            "[infrastructure]",
            "Base=abstract",
            "Extension",
            "",
            "[resources]",
            "patient=Patient",
            "adverseevent=AdverseEvent",
            "",
            "[draft-resources]",
            "AdverseEvent=1",
            "",
            "[logical]",
            "fivews",
            "",
            "[removed-resources]",
            "Animal",
            "",
            "[resource-infrastructure]",
            "resource=abstract,Resource",
            "",
            "[type-pages]",
            "narrative=narrative",
        ];

        List<ArtifactEntry> result = _parser.ParseLines(lines);

        // types: Annotation, CodeableConcept, SimpleQuantity (3)
        // infrastructure: Base (1 new), Extension (1 overrides type)
        // resources: Patient, AdverseEvent (2)
        // draft: merges into AdverseEvent
        // logical: fivews (1)
        // removed: Animal (1 new)
        // resource-infrastructure: Resource (1)
        // type-pages: ignored
        Assert.Equal(10, result.Count);

        Assert.Contains(result, e => e.Name == "AdverseEvent" && e.Modifiers.Contains("draft"));
        Assert.Contains(result, e => e.Name == "Animal" && e.Modifiers.Contains("removed"));
        Assert.Contains(result, e => e.Name == "Resource" && e.Category == "resource");
        Assert.Contains(result, e => e.Name == "fivews" && e.Category == "logical-model");
        Assert.DoesNotContain(result, e => e.Name == "Population");
        Assert.DoesNotContain(result, e => e.Name == "narrative");
    }
}
