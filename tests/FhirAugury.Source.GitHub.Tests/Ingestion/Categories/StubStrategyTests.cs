using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion.Categories;
using FhirAugury.Source.GitHub.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests.Ingestion.Categories;

public class StubStrategyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly GitHubDatabase _database;
    private readonly ArtifactFileMapper _mapper;

    public StubStrategyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "stub-strat-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _dbPath = Path.Combine(_tempDir, "test.db");
        _database = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _database.Initialize();
        _mapper = new ArtifactFileMapper(_database, NullLogger<ArtifactFileMapper>.Instance);
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // ── UTG Strategy ──────────────────────────────────────

    [Fact]
    public void Utg_Category_IsCorrect()
    {
        UtgStrategy strategy = new(_database, NullLogger<UtgStrategy>.Instance);
        Assert.Equal(RepoCategory.Utg, strategy.Category);
    }

    [Fact]
    public void Utg_GetPriorityPaths_ReturnsPaths()
    {
        UtgStrategy strategy = new(_database, NullLogger<UtgStrategy>.Instance);
        List<string>? paths = strategy.GetPriorityPaths("HL7/UTG", _tempDir);
        Assert.NotNull(paths);
        Assert.Equal(2, paths.Count);
        Assert.Contains("input/sourceOfTruth/", paths);
        Assert.Contains("input/resources/", paths);
    }

    [Fact]
    public void Utg_GetAdditionalIgnorePatterns_ReturnsPatterns()
    {
        UtgStrategy strategy = new(_database, NullLogger<UtgStrategy>.Instance);
        List<string> patterns = strategy.GetAdditionalIgnorePatterns();
        Assert.Equal(3, patterns.Count);
        Assert.Contains("input/sourceOfTruth/history/", patterns);
        Assert.Contains("input/sourceOfTruth/control-manifests/", patterns);
        Assert.Contains("input/sourceOfTruth/release-tracking/", patterns);
    }

    [Fact]
    public void Utg_Validate_TrueWhenSourceOfTruthExists()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "input", "sourceOfTruth"));
        UtgStrategy strategy = new(_database, NullLogger<UtgStrategy>.Instance);
        Assert.True(strategy.Validate("HL7/UTG", _tempDir));
    }

    [Fact]
    public void Utg_Validate_FalseWhenNoSourceOfTruth()
    {
        UtgStrategy strategy = new(_database, NullLogger<UtgStrategy>.Instance);
        Assert.False(strategy.Validate("HL7/UTG", _tempDir));
    }

    [Fact]
    public void Utg_DiscoverTags_VocabularyFamily()
    {
        UtgStrategy strategy = new(_database, NullLogger<UtgStrategy>.Instance);
        CreateFile("input/sourceOfTruth/fhir/codeSystems/CodeSystem-example.xml", "<CodeSystem xmlns=\"http://hl7.org/fhir\"><id value=\"example\"/></CodeSystem>");
        CreateFile("input/sourceOfTruth/v2/cs/cs-v2-0001.xml", "<CodeSystem xmlns=\"http://hl7.org/fhir\"><id value=\"v2-0001\"/></CodeSystem>");

        List<GitHubFileTagRecord> tags = strategy.DiscoverTags("HL7/UTG", _tempDir, CancellationToken.None);

        Assert.Contains(tags, t => t.TagCategory == "vocabulary-family" && t.TagName == "fhir");
        Assert.Contains(tags, t => t.TagCategory == "vocabulary-family" && t.TagName == "v2");
        Assert.Contains(tags, t => t.TagCategory == "fhir-resource-type" && t.TagName == "CodeSystem");
    }

    [Fact]
    public void Utg_DiscoverTags_RetiredArtifacts()
    {
        UtgStrategy strategy = new(_database, NullLogger<UtgStrategy>.Instance);
        CreateFile("input/sourceOfTruth/retired/codeSystems/old.xml", "<CodeSystem xmlns=\"http://hl7.org/fhir\"/>");

        List<GitHubFileTagRecord> tags = strategy.DiscoverTags("HL7/UTG", _tempDir, CancellationToken.None);

        Assert.Contains(tags, t =>
            t.TagCategory == "vocabulary-family" && t.TagName == "retired" && t.TagModifier == "retired");
        Assert.Contains(tags, t =>
            t.TagCategory == "fhir-resource-type" && t.TagName == "CodeSystem" && t.TagModifier == "retired");
    }

    [Fact]
    public void Utg_DetectXmlRootElement_ReturnsLocalName()
    {
        string xmlPath = Path.Combine(_tempDir, "test.xml");
        File.WriteAllText(xmlPath, "<ValueSet xmlns=\"http://hl7.org/fhir\"><id value=\"test\"/></ValueSet>");

        string? root = UtgStrategy.DetectXmlRootElement(xmlPath);
        Assert.Equal("ValueSet", root);
    }

    [Fact]
    public void Utg_DetectXmlRootElement_ReturnsNullForInvalid()
    {
        string txtPath = Path.Combine(_tempDir, "notxml.txt");
        File.WriteAllText(txtPath, "not xml content");

        string? root = UtgStrategy.DetectXmlRootElement(txtPath);
        Assert.Null(root);
    }

    // ── FhirExtensionsPack Strategy ───────────────────────

    [Fact]
    public void ExtPack_Category_IsCorrect()
    {
        FhirExtensionsPackStrategy strategy = new(_database, NullLogger<FhirExtensionsPackStrategy>.Instance);
        Assert.Equal(RepoCategory.FhirExtensionsPack, strategy.Category);
    }

    [Fact]
    public void ExtPack_GetPriorityPaths_ReturnsDefinitionsPath()
    {
        FhirExtensionsPackStrategy strategy = new(_database, NullLogger<FhirExtensionsPackStrategy>.Instance);
        List<string>? paths = strategy.GetPriorityPaths("HL7/fhir-extensions", _tempDir);
        Assert.NotNull(paths);
        Assert.Single(paths);
        Assert.Equal("input/definitions/", paths[0]);
    }

    [Fact]
    public void ExtPack_Validate_TrueWhenDefinitionsExist()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "input", "definitions"));
        FhirExtensionsPackStrategy strategy = new(_database, NullLogger<FhirExtensionsPackStrategy>.Instance);
        Assert.True(strategy.Validate("HL7/fhir-extensions", _tempDir));
    }

    [Fact]
    public void ExtPack_Validate_FalseWhenNoDefinitions()
    {
        FhirExtensionsPackStrategy strategy = new(_database, NullLogger<FhirExtensionsPackStrategy>.Instance);
        Assert.False(strategy.Validate("HL7/fhir-extensions", _tempDir));
    }

    [Fact]
    public void ExtPack_DiscoverTags_ResourceTypeAndTargetResource()
    {
        FhirExtensionsPackStrategy strategy = new(_database, NullLogger<FhirExtensionsPackStrategy>.Instance);
        CreateFile("input/definitions/Patient/StructureDefinition-patient-birthPlace.xml", "<StructureDefinition/>");
        CreateFile("input/definitions/datatypes/CodeSystem-example.xml", "<CodeSystem/>");

        List<GitHubFileTagRecord> tags = strategy.DiscoverTags("HL7/fhir-extensions", _tempDir, CancellationToken.None);

        Assert.Contains(tags, t => t.TagCategory == "fhir-resource-type" && t.TagName == "StructureDefinition");
        Assert.Contains(tags, t => t.TagCategory == "fhir-resource-type" && t.TagName == "CodeSystem");
        Assert.Contains(tags, t => t.TagCategory == "target-resource" && t.TagName == "Patient");
        Assert.Contains(tags, t => t.TagCategory == "target-resource" && t.TagName == "datatypes");
    }

    [Fact]
    public void ExtPack_GetAdditionalIgnorePatterns_ReturnsEmpty()
    {
        FhirExtensionsPackStrategy strategy = new(_database, NullLogger<FhirExtensionsPackStrategy>.Instance);
        Assert.Empty(strategy.GetAdditionalIgnorePatterns());
    }

    // ── Incubator Strategy ────────────────────────────────

    [Fact]
    public void Incubator_Category_IsCorrect()
    {
        IncubatorStrategy strategy = new(_database, NullLogger<IncubatorStrategy>.Instance);
        Assert.Equal(RepoCategory.Incubator, strategy.Category);
    }

    [Fact]
    public void Incubator_GetPriorityPaths_ReturnsPaths()
    {
        IncubatorStrategy strategy = new(_database, NullLogger<IncubatorStrategy>.Instance);
        List<string>? paths = strategy.GetPriorityPaths("HL7/test", _tempDir);
        Assert.NotNull(paths);
        Assert.Equal(2, paths.Count);
        Assert.Contains("input/resources/", paths);
        Assert.Contains("input/fsh/", paths);
    }

    [Fact]
    public void Incubator_GetAdditionalIgnorePatterns_ReturnsPatterns()
    {
        IncubatorStrategy strategy = new(_database, NullLogger<IncubatorStrategy>.Instance);
        List<string> patterns = strategy.GetAdditionalIgnorePatterns();
        Assert.Equal(2, patterns.Count);
        Assert.Contains("input/fsh/fsh-index.txt", patterns);
        Assert.Contains("fixme/", patterns);
    }

    [Fact]
    public void Incubator_Validate_TrueWhenSushiConfig()
    {
        File.WriteAllText(Path.Combine(_tempDir, "sushi-config.yaml"), "id: test");
        IncubatorStrategy strategy = new(_database, NullLogger<IncubatorStrategy>.Instance);
        Assert.True(strategy.Validate("HL7/test", _tempDir));
    }

    [Fact]
    public void Incubator_Validate_TrueWhenIgIni()
    {
        File.WriteAllText(Path.Combine(_tempDir, "ig.ini"), "[ig]");
        IncubatorStrategy strategy = new(_database, NullLogger<IncubatorStrategy>.Instance);
        Assert.True(strategy.Validate("HL7/test", _tempDir));
    }

    [Fact]
    public void Incubator_Validate_FalseWhenNoMarkers()
    {
        IncubatorStrategy strategy = new(_database, NullLogger<IncubatorStrategy>.Instance);
        Assert.False(strategy.Validate("HL7/test", _tempDir));
    }

    [Fact]
    public void Incubator_DiscoverTags_XmlArtifacts()
    {
        IncubatorStrategy strategy = new(_database, NullLogger<IncubatorStrategy>.Instance);
        CreateFile("sushi-config.yaml", "id: test\ncanonical: http://example.org");
        CreateFile("input/resources/StructureDefinition-example.xml", "<StructureDefinition/>");

        List<GitHubFileTagRecord> tags = strategy.DiscoverTags("HL7/test", _tempDir, CancellationToken.None);

        Assert.Contains(tags, t => t.TagCategory == "fhir-resource-type" && t.TagName == "StructureDefinition");
        Assert.Contains(tags, t => t.TagCategory == "incubator" && t.TagModifier == "incubated");
    }

    [Fact]
    public void Incubator_DiscoverTags_CoreNamespaceTag()
    {
        IncubatorStrategy strategy = new(_database, NullLogger<IncubatorStrategy>.Instance);
        CreateFile("sushi-config.yaml", "id: test\ncanonical: http://example.org\nspecial-url-base: http://hl7.org/fhir");
        CreateFile("input/resources/StructureDefinition-example.xml", "<StructureDefinition/>");

        List<GitHubFileTagRecord> tags = strategy.DiscoverTags("HL7/test", _tempDir, CancellationToken.None);

        Assert.Contains(tags, t => t.TagCategory == "core-namespace" && t.TagName == "incubated");
    }

    [Fact]
    public void Incubator_DiscoverTags_NoCoreNamespaceWithoutSpecialUrl()
    {
        IncubatorStrategy strategy = new(_database, NullLogger<IncubatorStrategy>.Instance);
        CreateFile("sushi-config.yaml", "id: test\ncanonical: http://example.org");
        CreateFile("input/resources/StructureDefinition-example.xml", "<StructureDefinition/>");

        List<GitHubFileTagRecord> tags = strategy.DiscoverTags("HL7/test", _tempDir, CancellationToken.None);

        Assert.DoesNotContain(tags, t => t.TagCategory == "core-namespace");
    }

    [Fact]
    public void Incubator_DiscoverTags_FshFiles()
    {
        IncubatorStrategy strategy = new(_database, NullLogger<IncubatorStrategy>.Instance);
        CreateFile("sushi-config.yaml", "id: test\ncanonical: http://example.org");
        CreateFile("input/fsh/example.fsh", "Profile: TestProfile\nParent: Patient");

        List<GitHubFileTagRecord> tags = strategy.DiscoverTags("HL7/test", _tempDir, CancellationToken.None);

        Assert.Contains(tags, t => t.TagCategory == "incubator" && t.TagModifier == "incubated"
            && t.FilePath.Contains("fsh/"));
    }

    // ── IG Strategy ───────────────────────────────────────

    [Fact]
    public void Ig_Category_IsCorrect()
    {
        IgStrategy strategy = new(_mapper, NullLogger<IgStrategy>.Instance);
        Assert.Equal(RepoCategory.Ig, strategy.Category);
    }

    [Fact]
    public void Ig_DiscoverTags_ReturnsEmpty()
    {
        IgStrategy strategy = new(_mapper, NullLogger<IgStrategy>.Instance);
        Assert.Empty(strategy.DiscoverTags("test/ig", _tempDir, CancellationToken.None));
    }

    [Fact]
    public void Ig_GetPriorityPaths_ReturnsNull()
    {
        IgStrategy strategy = new(_mapper, NullLogger<IgStrategy>.Instance);
        Assert.Null(strategy.GetPriorityPaths("test/ig", _tempDir));
    }

    [Fact]
    public void Ig_GetAdditionalIgnorePatterns_ReturnsEmpty()
    {
        IgStrategy strategy = new(_mapper, NullLogger<IgStrategy>.Instance);
        Assert.Empty(strategy.GetAdditionalIgnorePatterns());
    }

    [Fact]
    public void Ig_Validate_TrueWhenSushiConfig()
    {
        File.WriteAllText(Path.Combine(_tempDir, "sushi-config.yaml"), "id: test");
        IgStrategy strategy = new(_mapper, NullLogger<IgStrategy>.Instance);
        Assert.True(strategy.Validate("test/ig", _tempDir));
    }

    [Fact]
    public void Ig_Validate_FalseWhenNoMarkers()
    {
        IgStrategy strategy = new(_mapper, NullLogger<IgStrategy>.Instance);
        Assert.False(strategy.Validate("test/ig", _tempDir));
    }

    // ── Helper ────────────────────────────────────────────

    private void CreateFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
