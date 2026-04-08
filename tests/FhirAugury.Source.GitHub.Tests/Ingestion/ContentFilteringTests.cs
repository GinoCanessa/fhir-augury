using FhirAugury.Common.Indexing;
using FhirAugury.Common.Ingestion;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Indexing;
using FhirAugury.Source.GitHub.Ingestion;
using FhirAugury.Source.GitHub.Ingestion.Categories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Tests.Ingestion;

public class ContentFilteringTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly GitHubDatabase _database;

    public ContentFilteringTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cf-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _dbPath = Path.Combine(_tempDir, "test.db");
        _database = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _database.Initialize();
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // ── Cleanup Tests ─────────────────────────────────────

    [Fact]
    public void CleanupStaleFileContents_RemovesRecordsOutsidePriorityPaths()
    {
        // Arrange: insert records both inside and outside priority paths
        using SqliteConnection conn = _database.OpenConnection();
        InsertFileRecord(conn, "HL7/fhir", "source/patient/Patient.xml");
        InsertFileRecord(conn, "HL7/fhir", "source/observation/Observation.xml");
        InsertFileRecord(conn, "HL7/fhir", "qa/output.html");
        InsertFileRecord(conn, "HL7/fhir", "tools/build.gradle");
        InsertFileRecord(conn, "HL7/fhir", "implementations/java/test.java");

        Assert.Equal(5, GitHubFileContentRecord.SelectCount(conn, RepoFullName: "HL7/fhir"));

        // Act: cleanup with priority path "source/"
        GitHubIngestionPipeline pipeline = CreatePipeline();
        pipeline.CleanupStaleFileContents("HL7/fhir", ["source/"]);

        // Assert: only source/ files remain
        List<GitHubFileContentRecord> remaining = GitHubFileContentRecord.SelectList(conn, RepoFullName: "HL7/fhir");
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, r => Assert.StartsWith("source/", r.FilePath));
    }

    [Fact]
    public void CleanupStaleFileContents_MultiplePriorityPaths_KeepsAll()
    {
        // Arrange: UTG-like scenario with two priority paths
        using SqliteConnection conn = _database.OpenConnection();
        InsertFileRecord(conn, "HL7/UTG", "input/sourceOfTruth/fhir/CodeSystem.xml");
        InsertFileRecord(conn, "HL7/UTG", "input/resources/extension.xml");
        InsertFileRecord(conn, "HL7/UTG", "input/pagecontent/overview.md");
        InsertFileRecord(conn, "HL7/UTG", "input/includes/header.html");

        // Act
        GitHubIngestionPipeline pipeline = CreatePipeline();
        pipeline.CleanupStaleFileContents("HL7/UTG", ["input/sourceOfTruth/", "input/resources/"]);

        // Assert: only files within priority paths remain
        List<GitHubFileContentRecord> remaining = GitHubFileContentRecord.SelectList(conn, RepoFullName: "HL7/UTG");
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, r => r.FilePath == "input/sourceOfTruth/fhir/CodeSystem.xml");
        Assert.Contains(remaining, r => r.FilePath == "input/resources/extension.xml");
    }

    [Fact]
    public void CleanupStaleFileContents_NoStaleRecords_RemovesNothing()
    {
        // Arrange: all records are within priority paths
        using SqliteConnection conn = _database.OpenConnection();
        InsertFileRecord(conn, "HL7/fhir-extensions", "input/definitions/Patient/ext.xml");
        InsertFileRecord(conn, "HL7/fhir-extensions", "input/definitions/datatypes/dt.xml");

        // Act
        GitHubIngestionPipeline pipeline = CreatePipeline();
        pipeline.CleanupStaleFileContents("HL7/fhir-extensions", ["input/definitions/"]);

        // Assert: nothing removed
        Assert.Equal(2, GitHubFileContentRecord.SelectCount(conn, RepoFullName: "HL7/fhir-extensions"));
    }

    [Fact]
    public void CleanupStaleFileContents_DoesNotAffectOtherRepos()
    {
        // Arrange: records for two repos
        using SqliteConnection conn = _database.OpenConnection();
        InsertFileRecord(conn, "HL7/fhir", "source/Patient.xml");
        InsertFileRecord(conn, "HL7/fhir", "tools/build.gradle");
        InsertFileRecord(conn, "HL7/UTG", "tools/script.sh");

        // Act: cleanup only HL7/fhir
        GitHubIngestionPipeline pipeline = CreatePipeline();
        pipeline.CleanupStaleFileContents("HL7/fhir", ["source/"]);

        // Assert: HL7/UTG record is untouched
        Assert.Equal(1, GitHubFileContentRecord.SelectCount(conn, RepoFullName: "HL7/fhir"));
        Assert.Equal(1, GitHubFileContentRecord.SelectCount(conn, RepoFullName: "HL7/UTG"));
    }

    // ── Indexer Hard-Filter Tests ─────────────────────────

    [Fact]
    public void IndexRepositoryFiles_WithPriorityPaths_OnlyIndexesMatchingFiles()
    {
        // Arrange: create files inside and outside priority path
        string cloneDir = Path.Combine(_tempDir, "clone");
        CreateFile(cloneDir, "source/patient/Patient.xml", "<Patient xmlns=\"http://hl7.org/fhir\"/>");
        CreateFile(cloneDir, "source/observation/Observation.xml", "<Observation xmlns=\"http://hl7.org/fhir\"/>");
        CreateFile(cloneDir, "tools/build.gradle", "apply plugin: 'java'");
        CreateFile(cloneDir, "README.md", "# FHIR");

        GitHubFileContentIndexer indexer = CreateIndexer();

        // Act
        GitHubFileContentIndexer.FileIndexingResult result = indexer.IndexRepositoryFiles(
            "HL7/fhir", cloneDir, CancellationToken.None,
            priorityPaths: ["source/"]);

        // Assert: only source/ files indexed, others skipped by pattern
        Assert.Equal(2, result.Indexed);
        Assert.True(result.SkippedByPattern >= 2, $"Expected at least 2 skipped by pattern, got {result.SkippedByPattern}");

        using SqliteConnection conn = _database.OpenConnection();
        List<GitHubFileContentRecord> records = GitHubFileContentRecord.SelectList(conn, RepoFullName: "HL7/fhir");
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.StartsWith("source/", r.FilePath));
    }

    [Fact]
    public void IndexRepositoryFiles_WithIgnorePatterns_ExcludesMatchedFiles()
    {
        // Arrange: create files that match and don't match ignore patterns
        string cloneDir = Path.Combine(_tempDir, "clone2");
        CreateFile(cloneDir, "source/patient/Patient.xml", "<Patient xmlns=\"http://hl7.org/fhir\"/>");
        CreateFile(cloneDir, "source/patient/list-patient.xml", "<List xmlns=\"http://hl7.org/fhir\"/>");
        CreateFile(cloneDir, "source/patient/notes.txt", "some notes");

        GitHubFileContentIndexer indexer = CreateIndexer();

        // Act: with ignore patterns for list-*.xml and *.txt within source/
        GitHubFileContentIndexer.FileIndexingResult result = indexer.IndexRepositoryFiles(
            "HL7/fhir", cloneDir, CancellationToken.None,
            priorityPaths: ["source/"],
            additionalIgnorePatterns: ["source/**/list-*.xml", "source/**/*.txt"]);

        // Assert: only Patient.xml should be indexed
        Assert.Equal(1, result.Indexed);

        using SqliteConnection conn = _database.OpenConnection();
        List<GitHubFileContentRecord> records = GitHubFileContentRecord.SelectList(conn, RepoFullName: "HL7/fhir");
        Assert.Single(records);
        Assert.Equal("source/patient/Patient.xml", records[0].FilePath);
    }

    [Fact]
    public void IndexRepositoryFiles_NullPriorityPaths_IndexesAllFiles()
    {
        // Arrange
        string cloneDir = Path.Combine(_tempDir, "clone3");
        CreateFile(cloneDir, "source/Patient.xml", "<Patient xmlns=\"http://hl7.org/fhir\"/>");
        CreateFile(cloneDir, "tools/build.gradle", "apply plugin: 'java'");

        GitHubFileContentIndexer indexer = CreateIndexer();

        // Act: null priority paths means index all
        GitHubFileContentIndexer.FileIndexingResult result = indexer.IndexRepositoryFiles(
            "test/repo", cloneDir, CancellationToken.None,
            priorityPaths: null);

        // Assert: all files indexed
        Assert.True(result.Indexed >= 2, $"Expected at least 2 indexed, got {result.Indexed}");
    }

    // ── Helpers ───────────────────────────────────────────

    private GitHubIngestionPipeline CreatePipeline()
    {
        GitHubServiceOptions options = new()
        {
            SyncSchedule = "01:00:00",
        };

        return new GitHubIngestionPipeline(
            source: null!,
            database: _database,
            indexer: null!,
            cloner: null!,
            commitExtractor: null!,
            fileContentIndexer: null!,
            canonicalArtifactIndexer: null!,
            structureDefinitionIndexer: null!,
            fshArtifactIndexer: null!,
            categoryStrategies: [],
            weightResolver: null!,
            xrefRebuilder: null!,
            httpClientFactory: null!,
            tracker: null!,
            optionsAccessor: Options.Create(options),
            logger: NullLogger<GitHubIngestionPipeline>.Instance);
    }

    private GitHubFileContentIndexer CreateIndexer()
    {
        GitHubServiceOptions options = new()
        {
            FileContentIndexing = new FileContentIndexingOptions
            {
                Enabled = true,
                MaxFilesPerRepo = 10000,
                MaxFileSizeBytes = 1_000_000,
                MaxExtractedTextLength = 50_000,
            },
        };

        return new GitHubFileContentIndexer(
            _database,
            Options.Create(options),
            NullLogger<GitHubFileContentIndexer>.Instance);
    }

    private static void InsertFileRecord(SqliteConnection conn, string repo, string filePath)
    {
        GitHubFileContentRecord record = new()
        {
            Id = GitHubFileContentRecord.GetIndex(),
            RepoFullName = repo,
            FilePath = filePath,
            FileExtension = Path.GetExtension(filePath),
            ParserType = "xml",
            ContentText = "test content",
            ContentLength = 12,
            ExtractedLength = 12,
        };
        GitHubFileContentRecord.Insert(conn, record);
    }

    private static void CreateFile(string rootDir, string relativePath, string content)
    {
        string fullPath = Path.Combine(rootDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
