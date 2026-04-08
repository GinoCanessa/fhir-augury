using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion.Categories;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests.Ingestion.Categories;

public class FhirCoreStrategyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FhirCoreStrategy _strategy;

    public FhirCoreStrategyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fhir-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _strategy = new FhirCoreStrategy(NullLogger<FhirCoreStrategy>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Category_IsFhirCore()
    {
        Assert.Equal(Configuration.RepoCategory.FhirCore, _strategy.Category);
    }

    [Fact]
    public void StrategyName_IsFhirCore()
    {
        Assert.Equal("fhir-core", _strategy.StrategyName);
    }

    [Fact]
    public void Validate_ReturnsFalse_WhenNoFhirIni()
    {
        Assert.False(_strategy.Validate("HL7/fhir", _tempDir));
    }

    [Fact]
    public void Validate_ReturnsTrue_WhenFhirIniExists()
    {
        CreateFhirIni("[resources]\npatient=Patient");
        Assert.True(_strategy.Validate("HL7/fhir", _tempDir));
    }

    [Fact]
    public void GetPriorityPaths_ReturnsSourcePath()
    {
        List<string>? paths = _strategy.GetPriorityPaths("HL7/fhir", _tempDir);
        Assert.NotNull(paths);
        Assert.Single(paths);
        Assert.Equal("source/", paths[0]);
    }

    [Fact]
    public void GetAdditionalIgnorePatterns_ReturnsEmpty()
    {
        List<string> patterns = _strategy.GetAdditionalIgnorePatterns();
        Assert.Empty(patterns);
    }

    [Fact]
    public void DiscoverTags_ResourceDirectoryFiles()
    {
        CreateFhirIni("[resources]\npatient=Patient");
        CreateFile("source/patient/patient-introduction.xml", "<div/>");
        CreateFile("source/patient/patient.svg", "");
        CreateFile("source/patient/sub/example.json", "{}");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        List<GitHubFileTagRecord> resourceTags = tags.Where(t => t.TagCategory == "resource").ToList();
        Assert.Equal(3, resourceTags.Count);
        Assert.All(resourceTags, t =>
        {
            Assert.Equal("Patient", t.TagName);
            Assert.Equal("HL7/fhir", t.RepoFullName);
        });
    }

    [Fact]
    public void DiscoverTags_TypeWithDedicatedDirectory()
    {
        CreateFhirIni("[types]\nDosage");
        CreateFile("source/dosage/dosage.xml", "<type/>");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.Contains(tags, t => t.TagCategory == "type" && t.TagName == "Dosage");
    }

    [Fact]
    public void DiscoverTags_TypeFallbackToDatatypes()
    {
        CreateFhirIni("[types]\nCodeableConcept");
        CreateFile("source/datatypes/CodeableConcept.xml", "<type/>");
        CreateFile("source/datatypes/codeableconcept-example.json", "{}");
        CreateFile("source/datatypes/unrelated.xml", "<other/>");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        List<GitHubFileTagRecord> typeTags = tags.Where(t => t.TagCategory == "type").ToList();
        Assert.Equal(2, typeTags.Count);
        Assert.All(typeTags, t => Assert.Equal("CodeableConcept", t.TagName));
        Assert.DoesNotContain(tags, t => t.FilePath.Contains("unrelated"));
    }

    [Fact]
    public void DiscoverTags_LogicalModel()
    {
        CreateFhirIni("[logical]\nfivews");
        CreateFile("source/fivews/fivews.xml", "<logical/>");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.Contains(tags, t => t.TagCategory == "logical-model" && t.TagName == "fivews");
    }

    [Fact]
    public void DiscoverTags_InfrastructureCategory()
    {
        CreateFhirIni("[infrastructure]\nExtension");
        CreateFile("source/extension/extension.xml", "<infra/>");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.Contains(tags, t => t.TagCategory == "infrastructure" && t.TagName == "Extension");
    }

    [Fact]
    public void DiscoverTags_DraftModifier()
    {
        CreateFhirIni("[resources]\nadverseevent=AdverseEvent\n[draft-resources]\nAdverseEvent=1");
        CreateFile("source/adverseevent/example.xml", "<resource/>");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.Contains(tags, t =>
            t.TagCategory == "resource" &&
            t.TagName == "AdverseEvent" &&
            t.TagModifier == "draft");
    }

    [Fact]
    public void DiscoverTags_RemovedModifier()
    {
        CreateFhirIni("[removed-resources]\nAnimal");
        CreateFile("source/animal/animal.xml", "<removed/>");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.Contains(tags, t =>
            t.TagCategory == "resource" &&
            t.TagName == "Animal" &&
            t.TagModifier == "removed");
    }

    [Fact]
    public void DiscoverTags_RemovedResource_NoDirectoryNoTags()
    {
        CreateFhirIni("[removed-resources]\nActionDefinition");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.Empty(tags);
    }

    [Fact]
    public void DiscoverTags_XmlFileGetsResourceTypeTag()
    {
        CreateFhirIni("[resources]\npatient=Patient");
        CreateFile("source/patient/valueset-example.xml", "<ValueSet/>");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.Contains(tags, t =>
            t.TagCategory == "resource" && t.TagName == "Patient");
        Assert.Contains(tags, t =>
            t.TagCategory == "fhir-resource-type" && t.TagName == "ValueSet");
    }

    [Fact]
    public void DiscoverTags_StructureDefinitionXml()
    {
        CreateFhirIni("[resources]\npatient=Patient");
        CreateFile("source/patient/structuredefinition-patient.xml", "<SD/>");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.Contains(tags, t =>
            t.TagCategory == "fhir-resource-type" && t.TagName == "StructureDefinition");
    }

    [Fact]
    public void DiscoverTags_ClinicalResourcePrefix_NoResourceTypeTag()
    {
        CreateFhirIni("[resources]\npatient=Patient");
        CreateFile("source/patient/patient-introduction.xml", "<div/>");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.DoesNotContain(tags, t =>
            t.TagCategory == "fhir-resource-type" && t.TagName == "Patient");
    }

    [Fact]
    public void DiscoverTags_NonXmlFile_NoResourceTypeTag()
    {
        CreateFhirIni("[resources]\npatient=Patient");
        CreateFile("source/patient/README.md", "# Patient");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.DoesNotContain(tags, t => t.TagCategory == "fhir-resource-type");
    }

    [Fact]
    public void DiscoverTags_NoDashInFilename_NoResourceTypeTag()
    {
        CreateFhirIni("[resources]\npatient=Patient");
        CreateFile("source/patient/spreadsheet.xml", "<sheet/>");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.DoesNotContain(tags, t => t.TagCategory == "fhir-resource-type");
    }

    [Fact]
    public void DiscoverTags_CaseInsensitiveResourceType()
    {
        CreateFhirIni("[resources]\npatient=Patient");
        CreateFile("source/patient/ValueSet-example.xml", "<VS/>");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.Contains(tags, t =>
            t.TagCategory == "fhir-resource-type" && t.TagName == "ValueSet");
    }

    [Fact]
    public void DiscoverTags_CodeSystemV3()
    {
        CreateFhirIni("[resources]\npatient=Patient");
        CreateFile("source/patient/codesystem-v3-ActCode.xml", "<CS/>");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.Contains(tags, t =>
            t.TagCategory == "fhir-resource-type" && t.TagName == "CodeSystem");
    }

    [Fact]
    public void DiscoverTags_ForwardSlashPaths()
    {
        CreateFhirIni("[resources]\npatient=Patient");
        CreateFile("source/patient/example.xml", "<r/>");

        List<GitHubFileTagRecord> tags = _strategy.DiscoverTags("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.All(tags, t => Assert.DoesNotContain("\\", t.FilePath));
        Assert.All(tags, t => Assert.StartsWith("source/", t.FilePath));
    }

    private void CreateFhirIni(string content)
    {
        string dir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "fhir.ini"), content);
    }

    private void CreateFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
