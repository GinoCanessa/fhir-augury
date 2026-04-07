using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Tests.Ingestion;

/// <summary>
/// Integration test that exercises the full tagging pipeline:
/// INI parsing → directory scanning → resource type extraction → weight resolution → DB persistence.
/// </summary>
public class RepoFileTaggerIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly GitHubDatabase _database;

    public RepoFileTaggerIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tagger-int-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _dbPath = Path.Combine(_tempDir, "test.db");
        _database = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _database.Initialize();
    }

    public void Dispose()
    {
        _database.Dispose();
        // Clear SQLite connection pool to release file locks
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void FullPipeline_TagsFilesCorrectly()
    {
        // Arrange: create a mini HL7/fhir layout
        string clonePath = Path.Combine(_tempDir, "clone");
        SetupFhirRepoLayout(clonePath);

        TagWeightOptions weightOptions = new()
        {
            Default = 1.0,
            CategoryWeights = new()
            {
                ["resource"] = 1.0,
                ["type"] = 0.9,
                ["infrastructure"] = 0.8,
                ["logical-model"] = 0.7,
                ["fhir-resource-type"] = 0.5,
            },
            ModifierMultipliers = new()
            {
                ["draft"] = 0.7,
                ["removed"] = 0.3,
            },
        };

        FhirCoreDiscoveryPattern pattern = new(NullLogger<FhirCoreDiscoveryPattern>.Instance);
        TagWeightResolver resolver = new(Options.Create(weightOptions));
        RepoFileTagger tagger = new(
            [pattern],
            resolver,
            _database,
            NullLogger<RepoFileTagger>.Instance);

        // Act
        tagger.ApplyTags("HL7/fhir", clonePath, CancellationToken.None);

        // Assert
        using SqliteConnection connection = _database.OpenConnection();
        List<GitHubFileTagRecord> allTags = GitHubFileTagRecord.SelectList(connection);

        Assert.NotEmpty(allTags);

        // Patient files tagged as resource/Patient
        List<GitHubFileTagRecord> patientTags = allTags
            .Where(t => t.TagCategory == "resource" && t.TagName == "Patient")
            .ToList();
        Assert.Equal(3, patientTags.Count); // patient-intro.xml, structuredefinition-patient.xml, example.json
        Assert.All(patientTags, t => Assert.Equal(1.0, t.Weight));

        // AdverseEvent tagged with draft modifier
        List<GitHubFileTagRecord> draftTags = allTags
            .Where(t => t.TagName == "AdverseEvent" && t.TagModifier == "draft")
            .ToList();
        Assert.Single(draftTags);
        Assert.Equal(0.7, draftTags[0].Weight, 5); // 1.0 * 0.7

        // CodeableConcept type via datatypes fallback
        List<GitHubFileTagRecord> typeTags = allTags
            .Where(t => t.TagCategory == "type" && t.TagName == "CodeableConcept")
            .ToList();
        Assert.Single(typeTags);
        Assert.Equal(0.9, typeTags[0].Weight, 5);

        // fhir-resource-type tags for XML files
        List<GitHubFileTagRecord> resourceTypeTags = allTags
            .Where(t => t.TagCategory == "fhir-resource-type")
            .ToList();

        Assert.Contains(resourceTypeTags, t => t.TagName == "StructureDefinition");
        Assert.Contains(resourceTypeTags, t => t.TagName == "ValueSet");
        Assert.All(resourceTypeTags, t => Assert.Equal(0.5, t.Weight, 5));

        // patient-introduction.xml does NOT get fhir-resource-type tag
        Assert.DoesNotContain(allTags, t =>
            t.TagCategory == "fhir-resource-type" &&
            t.FilePath.Contains("patient-introduction"));

        // Logical model
        List<GitHubFileTagRecord> logicalTags = allTags
            .Where(t => t.TagCategory == "logical-model")
            .ToList();
        Assert.NotEmpty(logicalTags);
        Assert.All(logicalTags, t => Assert.Equal(0.7, t.Weight, 5));

        // Removed resource (Animal) — directory exists
        List<GitHubFileTagRecord> removedTags = allTags
            .Where(t => t.TagName == "Animal" && t.TagModifier == "removed")
            .ToList();
        Assert.Single(removedTags);
        Assert.Equal(0.3, removedTags[0].Weight, 5); // 1.0 * 0.3

        // Removed resource (BodySite) — no directory, no tags
        Assert.DoesNotContain(allTags, t => t.TagName == "BodySite");
    }

    [Fact]
    public void ApplyTags_ClearsExistingTags()
    {
        string clonePath = Path.Combine(_tempDir, "clone2");
        SetupMinimalRepoLayout(clonePath);

        FhirCoreDiscoveryPattern pattern = new(NullLogger<FhirCoreDiscoveryPattern>.Instance);
        TagWeightResolver resolver = new(Options.Create(new TagWeightOptions()));
        RepoFileTagger tagger = new(
            [pattern],
            resolver,
            _database,
            NullLogger<RepoFileTagger>.Instance);

        // Apply tags twice
        tagger.ApplyTags("HL7/fhir", clonePath, CancellationToken.None);
        tagger.ApplyTags("HL7/fhir", clonePath, CancellationToken.None);

        using SqliteConnection connection = _database.OpenConnection();
        List<GitHubFileTagRecord> allTags = GitHubFileTagRecord.SelectList(connection);

        // Should not have duplicates — second run clears first
        int patientCount = allTags.Count(t => t.TagName == "Patient" && t.TagCategory == "resource");
        Assert.Equal(1, patientCount);
    }

    private static void SetupFhirRepoLayout(string clonePath)
    {
        string ini = """
            [types]
            CodeableConcept

            [infrastructure]
            Extension

            [resources]
            patient=Patient
            adverseevent=AdverseEvent

            [draft-resources]
            AdverseEvent=1

            [logical]
            fivews

            [removed-resources]
            Animal
            BodySite

            [resource-infrastructure]
            resource=abstract,Resource
            """;

        CreateFile(clonePath, "source/fhir.ini", ini);

        // Patient directory with various files
        CreateFile(clonePath, "source/patient/patient-introduction.xml", "<div/>");
        CreateFile(clonePath, "source/patient/structuredefinition-patient.xml", "<StructureDefinition/>");
        CreateFile(clonePath, "source/patient/example.json", "{}");

        // AdverseEvent directory
        CreateFile(clonePath, "source/adverseevent/valueset-example.xml", "<ValueSet/>");

        // Extension (infrastructure)
        CreateFile(clonePath, "source/extension/extension.xml", "<Extension/>");

        // Logical model
        CreateFile(clonePath, "source/fivews/fivews.xml", "<StructureDefinition/>");

        // Removed resource (Animal has directory, BodySite does not)
        CreateFile(clonePath, "source/animal/animal.xml", "<removed/>");

        // Datatypes fallback for CodeableConcept
        CreateFile(clonePath, "source/datatypes/CodeableConcept.xml", "<type/>");

        // resource-infrastructure
        CreateFile(clonePath, "source/resource/resource.xml", "<Resource/>");
    }

    private static void SetupMinimalRepoLayout(string clonePath)
    {
        CreateFile(clonePath, "source/fhir.ini", "[resources]\npatient=Patient");
        CreateFile(clonePath, "source/patient/example.xml", "<Patient/>");
    }

    private static void CreateFile(string basePath, string relativePath, string content)
    {
        string fullPath = Path.Combine(basePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
