using FhirAugury.Server.Terminology.Matching;
using FhirAugury.Server.Terminology.Models;
using Hl7.Fhir.Model;

namespace FhirAugury.Server.Terminology.Tests.Matching;

public class SubmissionNormalizerTests
{
    [Fact]
    public void Normalize_CodeSystem_FlattensTreeAndCarriesMetadata()
    {
        SubmissionNormalizer norm = new(conceptCap: 100);

        CodeSystem cs = new()
        {
            Url = "http://example.org/cs",
            Version = "1.0.0",
            Title = "Example",
            Name = "ExampleCS",
            Description = "An example CodeSystem.",
            Concept = new List<CodeSystem.ConceptDefinitionComponent>
            {
                new()
                {
                    Code = "parent",
                    Display = "Parent",
                    Concept = new List<CodeSystem.ConceptDefinitionComponent>
                    {
                        new() { Code = "child", Display = "Child" },
                    },
                },
            },
        };

        NormalizedSubmission sub = norm.Normalize(cs, fhirVersion: "R4");

        Assert.Equal("CodeSystem", sub.Kind);
        Assert.Equal("R4", sub.FhirVersion);
        Assert.Equal("http://example.org/cs", sub.CanonicalUrl);
        Assert.Equal("http://example.org/cs", sub.CanonicalUrlNormalized);
        Assert.Equal("Example", sub.Title);
        Assert.Equal(2, sub.Concepts.Count);
        Assert.Contains(sub.Concepts, c => c.Code == "child" && c.Display == "Child");
    }

    [Fact]
    public void Normalize_ValueSet_FlattensComposeInclude()
    {
        SubmissionNormalizer norm = new(conceptCap: 100);

        ValueSet vs = new()
        {
            Url = "http://example.org/vs",
            Compose = new ValueSet.ComposeComponent
            {
                Include = new List<ValueSet.ConceptSetComponent>
                {
                    new()
                    {
                        System = "http://example.org/cs",
                        Concept = new List<ValueSet.ConceptReferenceComponent>
                        {
                            new() { Code = "a", Display = "Alpha" },
                            new() { Code = "b", Display = "Beta" },
                        },
                    },
                },
            },
        };

        NormalizedSubmission sub = norm.Normalize(vs, fhirVersion: "R5");

        Assert.Equal("ValueSet", sub.Kind);
        Assert.Equal(2, sub.Concepts.Count);
        Assert.All(sub.Concepts, c => Assert.Equal("http://example.org/cs", c.SystemUrl));
    }

    [Fact]
    public void Normalize_OverCap_Throws()
    {
        SubmissionNormalizer norm = new(conceptCap: 2);

        CodeSystem cs = new()
        {
            Url = "http://example.org/cs",
            Concept = new List<CodeSystem.ConceptDefinitionComponent>
            {
                new() { Code = "a" }, new() { Code = "b" }, new() { Code = "c" },
            },
        };

        SubmissionTooLargeException ex = Assert.Throws<SubmissionTooLargeException>(
            () => norm.Normalize(cs, fhirVersion: "R4"));
        Assert.Equal(2, ex.Cap);
        Assert.True(ex.Submitted > 2);
    }
}
