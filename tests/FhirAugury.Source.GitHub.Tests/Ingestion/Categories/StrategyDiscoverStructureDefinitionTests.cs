using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Ingestion.Categories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests.Ingestion.Categories;

public class StrategyDiscoverStructureDefinitionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly GitHubDatabase _database;

    public StrategyDiscoverStructureDefinitionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sd-strategy-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _dbPath = Path.Combine(_tempDir, "test.db");
        _database = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _database.Initialize();
    }

    public void Dispose()
    {
        _database.Dispose();
        TestFileCleanup.SafeDeleteDirectory(_tempDir);
    }

    // ────────────────────────────────────────────────────────
    // FhirCoreStrategy
    // ────────────────────────────────────────────────────────

    [Fact]
    public void FhirCore_DiscoversSdFiles_UnderSource()
    {
        CreateFile("source/patient/structuredefinition-patient.xml", "<SD/>");
        CreateFile("source/observation/structuredefinition-observation.xml", "<SD/>");
        CreateFile("source/patient/valueset-example.xml", "<VS/>"); // not an SD
        CreateFile("source/fhir.ini", "[resources]\npatient=Patient");

        FhirCoreStrategy strategy = new(NullLogger<FhirCoreStrategy>.Instance);
        List<string> files = strategy.DiscoverStructureDefinitionFiles("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.Contains("structuredefinition-", Path.GetFileName(f)));
    }

    [Fact]
    public void FhirCore_ReturnsEmpty_WhenNoSourceDir()
    {
        FhirCoreStrategy strategy = new(NullLogger<FhirCoreStrategy>.Instance);
        List<string> files = strategy.DiscoverStructureDefinitionFiles("HL7/fhir", _tempDir, CancellationToken.None);

        Assert.Empty(files);
    }

    // ────────────────────────────────────────────────────────
    // FhirExtensionsPackStrategy
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ExtensionsPack_DiscoversSdFiles_UnderInputDefinitions()
    {
        CreateFile("input/definitions/extensions/StructureDefinition-patient-birthPlace.xml", "<SD/>");
        CreateFile("input/definitions/datatypes/StructureDefinition-iso21090-EN.xml", "<SD/>");
        CreateFile("input/definitions/extensions/CodeSystem-example.xml", "<CS/>"); // not an SD

        FhirExtensionsPackStrategy strategy = new(NullLogger<FhirExtensionsPackStrategy>.Instance);
        List<string> files = strategy.DiscoverStructureDefinitionFiles("HL7/fhir-extensions", _tempDir, CancellationToken.None);

        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.StartsWith("StructureDefinition-", Path.GetFileName(f)));
    }

    [Fact]
    public void ExtensionsPack_ReturnsEmpty_WhenNoDefinitionsDir()
    {
        FhirExtensionsPackStrategy strategy = new(NullLogger<FhirExtensionsPackStrategy>.Instance);
        List<string> files = strategy.DiscoverStructureDefinitionFiles("HL7/fhir-extensions", _tempDir, CancellationToken.None);

        Assert.Empty(files);
    }

    // ────────────────────────────────────────────────────────
    // UtgStrategy
    // ────────────────────────────────────────────────────────

    [Fact]
    public void Utg_DiscoversSdFiles_UnderInputResources()
    {
        CreateFile("input/resources/StructureDefinition-example.xml", "<SD/>");
        CreateFile("input/resources/CodeSystem-example.xml", "<CS/>"); // not an SD

        UtgStrategy strategy = new(NullLogger<UtgStrategy>.Instance);
        List<string> files = strategy.DiscoverStructureDefinitionFiles("HL7/UTG", _tempDir, CancellationToken.None);

        Assert.Single(files);
        Assert.StartsWith("StructureDefinition-", Path.GetFileName(files[0]));
    }

    [Fact]
    public void Utg_ReturnsEmpty_WhenNoResourcesDir()
    {
        UtgStrategy strategy = new(NullLogger<UtgStrategy>.Instance);
        List<string> files = strategy.DiscoverStructureDefinitionFiles("HL7/UTG", _tempDir, CancellationToken.None);

        Assert.Empty(files);
    }

    // ────────────────────────────────────────────────────────
    // IncubatorStrategy
    // ────────────────────────────────────────────────────────

    [Fact]
    public void Incubator_DiscoversSdFiles_UnderInputResources()
    {
        CreateFile("input/resources/profiles/StructureDefinition-example.xml", "<SD/>");
        CreateFile("input/resources/StructureDefinition-direct.xml", "<SD/>");

        IncubatorStrategy strategy = new(NullLogger<IncubatorStrategy>.Instance);
        List<string> files = strategy.DiscoverStructureDefinitionFiles("HL7/some-incubator", _tempDir, CancellationToken.None);

        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void Incubator_FallsBackToInput_WhenNoResourcesDir()
    {
        CreateFile("input/StructureDefinition-fallback.xml", "<SD/>");

        IncubatorStrategy strategy = new(NullLogger<IncubatorStrategy>.Instance);
        List<string> files = strategy.DiscoverStructureDefinitionFiles("HL7/some-incubator", _tempDir, CancellationToken.None);

        Assert.Single(files);
    }

    [Fact]
    public void Incubator_ReturnsEmpty_WhenNoInputDir()
    {
        IncubatorStrategy strategy = new(NullLogger<IncubatorStrategy>.Instance);
        List<string> files = strategy.DiscoverStructureDefinitionFiles("HL7/some-incubator", _tempDir, CancellationToken.None);

        Assert.Empty(files);
    }

    // ────────────────────────────────────────────────────────
    // IgStrategy
    // ────────────────────────────────────────────────────────

    [Fact]
    public void Ig_ReturnsEmpty_ByDefault()
    {
        IgStrategy strategy = new(null!, NullLogger<IgStrategy>.Instance);
        List<string> files = strategy.DiscoverStructureDefinitionFiles("HL7/some-ig", _tempDir, CancellationToken.None);

        Assert.Empty(files);
    }

    // ────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────

    private void CreateFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
