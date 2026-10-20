using FhirAugury.Server.Terminology;
using FhirAugury.Server.Terminology.Ingestion;
using Hl7.Fhir.Model;

namespace FhirAugury.Server.Terminology.Tests;

/// <summary>
/// Unit tests for <see cref="TerminologyArtifactNormalizer"/>.
/// Confirms canonical-URL normalization, status string capture,
/// and recursive CodeSystem.concept tree flattening.
/// </summary>
public class TerminologyArtifactNormalizerTests
{
    private const string FakeJson = "{}";

    [Fact]
    public void Normalize_CodeSystem_FlattensConceptTreeAndNormalizesUrl()
    {
        TerminologyArtifactNormalizer norm = new();

        CodeSystem cs = new()
        {
            Url = "HTTP://Example.org/CS/  ",
            Version = "1.0.0",
            Title = "Example",
            Name = "Example",
            Status = PublicationStatus.Active,
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
                new() { Code = "leaf", Display = "Leaf" },
            },
        };

        (var artifact, var concepts) = norm.Normalize(cs, "test.pkg", "1.0.0", "R4", FakeJson);

        Assert.Equal("CodeSystem", artifact.Kind);
        Assert.Equal("HTTP://Example.org/CS/  ", artifact.CanonicalUrl);
        // NormalizeCanonicalUrl trims, lowercases, strips trailing slash.
        Assert.Equal("http://example.org/cs", artifact.CanonicalUrlNormalized);
        Assert.Equal("Active", artifact.Status);
        Assert.Equal("test.pkg", artifact.PackageId);
        Assert.Equal(FakeJson, artifact.Json);

        // 3 nodes: parent + child + leaf
        Assert.Equal(3, concepts.Count);
        // ArtifactId is set to 0 by the normalizer; the pipeline rewrites
        // it to the inserted artifact row Id before persistence.
        Assert.All(concepts, c => Assert.Equal(0, c.ArtifactId));
        // Concept SystemUrl mirrors the CodeSystem.url as declared.
        Assert.All(concepts, c => Assert.Equal(cs.Url, c.SystemUrl));
        Assert.Contains(concepts, c => c.Code == "child" && c.Display == "Child");
    }

    [Fact]
    public void Normalize_ValueSet_FlattensComposeIncludeConcepts()
    {
        TerminologyArtifactNormalizer norm = new();

        ValueSet vs = new()
        {
            Url = "http://example.org/vs",
            Version = "1.0.0",
            Status = PublicationStatus.Draft,
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

        (var artifact, var concepts) = norm.Normalize(vs, "test.pkg", "1.0.0", "R4", FakeJson);

        Assert.Equal("ValueSet", artifact.Kind);
        Assert.Equal("Draft", artifact.Status);
        Assert.Equal(2, concepts.Count);
        Assert.All(concepts, c => Assert.Equal("http://example.org/cs", c.SystemUrl));
        Assert.Contains(concepts, c => c.Code == "a");
        Assert.Contains(concepts, c => c.Code == "b");
    }
}
