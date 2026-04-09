using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

public class StructureDefinitionIndexerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;
    private readonly StructureDefinitionIndexer _indexer;
    private readonly string _tempDir;

    public StructureDefinitionIndexerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sd_indexer_test_{Guid.NewGuid()}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
        _indexer = new StructureDefinitionIndexer(NullLogger<StructureDefinitionIndexer>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"sd_indexer_clone_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void IndexStructureDefinitions_ValidSdFile_InsertsRecord()
    {
        string sdXml = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://hl7.org/fhir/StructureDefinition/Patient"/>
              <name value="Patient"/>
              <title value="Patient"/>
              <status value="active"/>
              <kind value="resource"/>
              <abstract value="false"/>
              <type value="Patient"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/DomainResource"/>
              <derivation value="specialization"/>
              <differential>
                <element>
                  <id value="Patient"/>
                  <path value="Patient"/>
                  <short value="Information about an individual"/>
                </element>
              </differential>
            </StructureDefinition>
            """;

        string filePath = CreateFile("source/patient/structuredefinition-patient.xml", sdXml);

        using SqliteConnection connection = _db.OpenConnection();
        _indexer.IndexStructureDefinitions("HL7/fhir", [filePath], _tempDir, connection, CancellationToken.None);

        List<GitHubStructureDefinitionRecord> results = GitHubStructureDefinitionRecord.SelectList(connection, RepoFullName: "HL7/fhir");
        Assert.Single(results);
        Assert.Equal("Patient", results[0].Name);
        Assert.Equal("Resource", results[0].ArtifactClass);
        Assert.Equal("source/patient/structuredefinition-patient.xml", results[0].FilePath);
    }

    [Fact]
    public void IndexStructureDefinitions_ExtensionSd_ClassifiedAsExtension()
    {
        string sdXml = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://hl7.org/fhir/StructureDefinition/patient-birthPlace"/>
              <name value="patient-birthPlace"/>
              <title value="Patient Birth Place"/>
              <status value="active"/>
              <kind value="complex-type"/>
              <abstract value="false"/>
              <type value="Extension"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/Extension"/>
              <derivation value="constraint"/>
              <context>
                <type value="element"/>
                <expression value="Patient"/>
              </context>
              <differential>
                <element>
                  <id value="Extension"/>
                  <path value="Extension"/>
                  <short value="Place of birth"/>
                </element>
              </differential>
            </StructureDefinition>
            """;

        string filePath = CreateFile("source/patient/structuredefinition-patient-birthplace.xml", sdXml);

        using SqliteConnection connection = _db.OpenConnection();
        _indexer.IndexStructureDefinitions("HL7/fhir", [filePath], _tempDir, connection, CancellationToken.None);

        List<GitHubStructureDefinitionRecord> results = GitHubStructureDefinitionRecord.SelectList(connection, RepoFullName: "HL7/fhir");
        Assert.Single(results);
        Assert.Equal("Extension", results[0].ArtifactClass);
        Assert.NotNull(results[0].Contexts);
    }

    [Fact]
    public void IndexStructureDefinitions_InvalidFile_SkipsAndLogs()
    {
        string filePath = CreateFile("source/bad/structuredefinition-bad.xml", "<NotAStructureDefinition/>");

        using SqliteConnection connection = _db.OpenConnection();
        _indexer.IndexStructureDefinitions("HL7/fhir", [filePath], _tempDir, connection, CancellationToken.None);

        List<GitHubStructureDefinitionRecord> results = GitHubStructureDefinitionRecord.SelectList(connection, RepoFullName: "HL7/fhir");
        Assert.Empty(results);
    }

    [Fact]
    public void IndexStructureDefinitions_ClearsExistingRecordsForRepo()
    {
        string sdXml = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://hl7.org/fhir/StructureDefinition/Patient"/>
              <name value="Patient"/>
              <status value="active"/>
              <kind value="resource"/>
              <abstract value="false"/>
              <type value="Patient"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/DomainResource"/>
              <derivation value="specialization"/>
              <differential/>
            </StructureDefinition>
            """;

        string filePath = CreateFile("source/patient/structuredefinition-patient.xml", sdXml);

        using SqliteConnection connection = _db.OpenConnection();

        // First run
        _indexer.IndexStructureDefinitions("HL7/fhir", [filePath], _tempDir, connection, CancellationToken.None);
        Assert.Single(GitHubStructureDefinitionRecord.SelectList(connection, RepoFullName: "HL7/fhir"));

        // Second run — should clear and re-insert
        _indexer.IndexStructureDefinitions("HL7/fhir", [filePath], _tempDir, connection, CancellationToken.None);
        Assert.Single(GitHubStructureDefinitionRecord.SelectList(connection, RepoFullName: "HL7/fhir"));
    }

    [Fact]
    public void IndexStructureDefinitions_CreatesFileTagRecords()
    {
        string sdXml = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://hl7.org/fhir/StructureDefinition/Patient"/>
              <name value="Patient"/>
              <status value="active"/>
              <kind value="resource"/>
              <abstract value="false"/>
              <type value="Patient"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/DomainResource"/>
              <derivation value="specialization"/>
              <differential/>
            </StructureDefinition>
            """;

        string filePath = CreateFile("source/patient/structuredefinition-patient.xml", sdXml);

        using SqliteConnection connection = _db.OpenConnection();
        _indexer.IndexStructureDefinitions("HL7/fhir", [filePath], _tempDir, connection, CancellationToken.None);

        List<GitHubFileTagRecord> tags = GitHubFileTagRecord.SelectList(connection, TagCategory: "artifact-class");
        Assert.Single(tags);
        Assert.Equal("Resource", tags[0].TagName);
    }

    [Fact]
    public void IndexStructureDefinitions_CreatesSpecFileMapRecords()
    {
        string sdXml = """
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <url value="http://hl7.org/fhir/StructureDefinition/Patient"/>
              <name value="Patient"/>
              <status value="active"/>
              <kind value="resource"/>
              <abstract value="false"/>
              <type value="Patient"/>
              <baseDefinition value="http://hl7.org/fhir/StructureDefinition/DomainResource"/>
              <derivation value="specialization"/>
              <differential/>
            </StructureDefinition>
            """;

        string filePath = CreateFile("source/patient/structuredefinition-patient.xml", sdXml);

        using SqliteConnection connection = _db.OpenConnection();
        _indexer.IndexStructureDefinitions("HL7/fhir", [filePath], _tempDir, connection, CancellationToken.None);

        List<GitHubSpecFileMapRecord> maps = GitHubSpecFileMapRecord.SelectList(connection, MapType: "structuredefinition");
        Assert.Single(maps);
        Assert.Equal("http://hl7.org/fhir/StructureDefinition/Patient", maps[0].ArtifactKey);
    }

    [Fact]
    public void IndexStructureDefinitions_EmptyList_NoError()
    {
        using SqliteConnection connection = _db.OpenConnection();
        _indexer.IndexStructureDefinitions("HL7/fhir", [], _tempDir, connection, CancellationToken.None);

        List<GitHubStructureDefinitionRecord> results = GitHubStructureDefinitionRecord.SelectList(connection);
        Assert.Empty(results);
    }

    private string CreateFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }
}
