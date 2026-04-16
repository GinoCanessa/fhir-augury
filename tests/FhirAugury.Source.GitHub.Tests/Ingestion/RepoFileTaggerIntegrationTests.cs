using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion.Categories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Tests.Ingestion;

/// <summary>
/// Integration test that exercises the full tagging pipeline:
/// INI parsing → directory scanning → resource type extraction → weight resolution → DB persistence.
/// Migrated from RepoFileTagger to the new strategy + inline tag logic pattern.
/// </summary>
public class TaggingIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly GitHubDatabase _database;

    public TaggingIntegrationTests()
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
        TestFileCleanup.SafeDeleteDirectory(_tempDir);
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

        FhirCoreStrategy strategy = new(NullLogger<FhirCoreStrategy>.Instance);
        TagWeightResolver resolver = new(Options.Create(weightOptions));

        // Act — replicate the pipeline's inline ApplyTags logic
        using SqliteConnection connection = _database.OpenConnection();
        ClearTags(connection, "HL7/fhir");

        List<GitHubFileTagRecord> tags = strategy.DiscoverTags("HL7/fhir", clonePath, CancellationToken.None);
        foreach (GitHubFileTagRecord tag in tags)
        {
            tag.Weight = resolver.ResolveWeight(tag.TagCategory, tag.TagName, tag.TagModifier);
        }

        const int batchSize = 1000;
        for (int i = 0; i < tags.Count; i += batchSize)
        {
            List<GitHubFileTagRecord> batch = tags.GetRange(i, Math.Min(batchSize, tags.Count - i));
            batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        // Assert
        List<GitHubFileTagRecord> allTags = GitHubFileTagRecord.SelectList(connection);

        Assert.NotEmpty(allTags);

        // Patient files tagged as resource/Patient
        List<GitHubFileTagRecord> patientTags = allTags
            .Where(t => t.TagCategory == "resource" && t.TagName == "Patient")
            .ToList();
        Assert.Equal(3, patientTags.Count);
        Assert.All(patientTags, t => Assert.Equal(1.0, t.Weight));

        // AdverseEvent tagged with draft modifier
        List<GitHubFileTagRecord> draftTags = allTags
            .Where(t => t.TagName == "AdverseEvent" && t.TagModifier == "draft")
            .ToList();
        Assert.Single(draftTags);
        Assert.Equal(0.7, draftTags[0].Weight, 5);

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
        Assert.Equal(0.3, removedTags[0].Weight, 5);

        // Removed resource (BodySite) — no directory, no tags
        Assert.DoesNotContain(allTags, t => t.TagName == "BodySite");
    }

    [Fact]
    public void ApplyTags_ClearsExistingTags()
    {
        string clonePath = Path.Combine(_tempDir, "clone2");
        SetupMinimalRepoLayout(clonePath);

        FhirCoreStrategy strategy = new(NullLogger<FhirCoreStrategy>.Instance);
        TagWeightResolver resolver = new(Options.Create(new TagWeightOptions()));

        // Apply tags twice
        ApplyTagsViaStrategy(strategy, resolver, "HL7/fhir", clonePath);
        ApplyTagsViaStrategy(strategy, resolver, "HL7/fhir", clonePath);

        using SqliteConnection connection = _database.OpenConnection();
        List<GitHubFileTagRecord> allTags = GitHubFileTagRecord.SelectList(connection);

        // Should not have duplicates — second run clears first
        int patientCount = allTags.Count(t => t.TagName == "Patient" && t.TagCategory == "resource");
        Assert.Equal(1, patientCount);
    }

    private void ApplyTagsViaStrategy(FhirCoreStrategy strategy, TagWeightResolver resolver, string repoFullName, string clonePath)
    {
        using SqliteConnection connection = _database.OpenConnection();
        ClearTags(connection, repoFullName);

        List<GitHubFileTagRecord> tags = strategy.DiscoverTags(repoFullName, clonePath, CancellationToken.None);
        foreach (GitHubFileTagRecord tag in tags)
        {
            tag.Weight = resolver.ResolveWeight(tag.TagCategory, tag.TagName, tag.TagModifier);
        }

        if (tags.Count > 0)
        {
            tags.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
        }
    }

    private static void ClearTags(SqliteConnection connection, string repoFullName)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM github_file_tags WHERE RepoFullName = @repo";
        cmd.Parameters.AddWithValue("@repo", repoFullName);
        cmd.ExecuteNonQuery();
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
        CreateFile(clonePath, "source/patient/patient-introduction.xml", "<div/>");
        CreateFile(clonePath, "source/patient/structuredefinition-patient.xml", "<StructureDefinition/>");
        CreateFile(clonePath, "source/patient/example.json", "{}");
        CreateFile(clonePath, "source/adverseevent/valueset-example.xml", "<ValueSet/>");
        CreateFile(clonePath, "source/extension/extension.xml", "<Extension/>");
        CreateFile(clonePath, "source/fivews/fivews.xml", "<StructureDefinition/>");
        CreateFile(clonePath, "source/animal/animal.xml", "<removed/>");
        CreateFile(clonePath, "source/datatypes/CodeableConcept.xml", "<type/>");
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
