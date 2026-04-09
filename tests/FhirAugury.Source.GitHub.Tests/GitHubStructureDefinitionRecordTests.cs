using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

public class GitHubStructureDefinitionRecordTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;

    public GitHubStructureDefinitionRecordTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"github_sd_test_{Guid.NewGuid()}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Initialize_CreatesStructureDefinitionsTable()
    {
        using SqliteConnection conn = _db.OpenConnection();
        List<string> tables = GetTableNames(conn);
        Assert.Contains("github_structure_definitions", tables);
    }

    [Fact]
    public void Initialize_CreatesFtsTable()
    {
        using SqliteConnection conn = _db.OpenConnection();
        List<string> tables = GetTableNames(conn);
        Assert.Contains("github_structure_definitions_fts", tables);
    }

    [Fact]
    public void InsertAndSelect_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubStructureDefinitionRecord record = new()
        {
            Id = GitHubStructureDefinitionRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            FilePath = "source/patient/structuredefinition-patient.xml",
            Url = "http://hl7.org/fhir/StructureDefinition/Patient",
            Name = "Patient",
            Title = "Patient",
            Status = "active",
            ArtifactClass = "Resource",
            Kind = "resource",
            IsAbstract = 0,
            FhirType = "Patient",
            BaseDefinition = "http://hl7.org/fhir/StructureDefinition/DomainResource",
            Derivation = "specialization",
            FhirVersion = "4.0.1",
            Description = "Demographics and other administrative information about an individual",
            Publisher = "HL7 FHIR Standard",
            WorkGroup = "pa",
            FhirMaturity = 5,
            StandardsStatus = "normative",
            Category = null,
            Contexts = null,
        };

        GitHubStructureDefinitionRecord.Insert(conn, record);

        List<GitHubStructureDefinitionRecord> results = GitHubStructureDefinitionRecord.SelectList(conn, RepoFullName: "HL7/fhir");
        Assert.Single(results);

        GitHubStructureDefinitionRecord r = results[0];
        Assert.Equal("Patient", r.Name);
        Assert.Equal("http://hl7.org/fhir/StructureDefinition/Patient", r.Url);
        Assert.Equal("Resource", r.ArtifactClass);
        Assert.Equal("resource", r.Kind);
        Assert.Equal(0, r.IsAbstract);
        Assert.Equal("pa", r.WorkGroup);
        Assert.Equal(5, r.FhirMaturity);
        Assert.Equal("normative", r.StandardsStatus);
    }

    [Fact]
    public void SelectByName_ReturnsMatchingRecords()
    {
        using SqliteConnection conn = _db.OpenConnection();

        InsertSd(conn, "Patient", "Resource", "resource");
        InsertSd(conn, "Observation", "Resource", "resource");

        List<GitHubStructureDefinitionRecord> results = GitHubStructureDefinitionRecord.SelectList(conn, Name: "Patient");
        Assert.Single(results);
        Assert.Equal("Patient", results[0].Name);
    }

    [Fact]
    public void SelectByArtifactClass_ReturnsMatchingRecords()
    {
        using SqliteConnection conn = _db.OpenConnection();

        InsertSd(conn, "Patient", "Resource", "resource");
        InsertSd(conn, "us-core-patient", "Profile", "resource", filePath: "p.xml");

        List<GitHubStructureDefinitionRecord> resources = GitHubStructureDefinitionRecord.SelectList(conn, ArtifactClass: "Resource");
        Assert.Single(resources);

        List<GitHubStructureDefinitionRecord> profiles = GitHubStructureDefinitionRecord.SelectList(conn, ArtifactClass: "Profile");
        Assert.Single(profiles);
    }

    [Fact]
    public void Indexes_AreCreated()
    {
        using SqliteConnection conn = _db.OpenConnection();

        // Verify that indexes exist on the table
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name, sql FROM sqlite_master
            WHERE type = 'index' AND tbl_name = 'github_structure_definitions'
            ORDER BY name
            """;
        using SqliteDataReader reader = cmd.ExecuteReader();

        List<string> indexNames = [];
        while (reader.Read())
        {
            indexNames.Add(reader.GetString(0));
        }

        // Should have indexes for RepoFullName+Url, RepoFullName+Name, etc.
        Assert.NotEmpty(indexNames);
    }

    [Fact]
    public void ResetDatabase_DropsAndRecreatesTable()
    {
        using SqliteConnection conn = _db.OpenConnection();
        InsertSd(conn, "Patient", "Resource", "resource");
        Assert.Single(GitHubStructureDefinitionRecord.SelectList(conn, RepoFullName: "HL7/fhir"));

        _db.ResetDatabase();

        using SqliteConnection conn2 = _db.OpenConnection();
        Assert.Empty(GitHubStructureDefinitionRecord.SelectList(conn2));
        Assert.Contains("github_structure_definitions", GetTableNames(conn2));
    }

    private static void InsertSd(SqliteConnection conn, string name, string artifactClass, string kind, string? filePath = null)
    {
        GitHubStructureDefinitionRecord record = CreateRecord(name, artifactClass, kind, filePath);
        GitHubStructureDefinitionRecord.Insert(conn, record);
    }

    private static GitHubStructureDefinitionRecord CreateRecord(string name, string artifactClass, string kind, string? filePath = null)
    {
        return new()
        {
            Id = GitHubStructureDefinitionRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            FilePath = filePath ?? $"source/{name.ToLowerInvariant()}/structuredefinition-{name.ToLowerInvariant()}.xml",
            Url = $"http://hl7.org/fhir/StructureDefinition/{name}",
            Name = name,
            ArtifactClass = artifactClass,
            Kind = kind,
        };
    }

    private static List<string> GetTableNames(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'trigger') ORDER BY name";
        using SqliteDataReader reader = cmd.ExecuteReader();
        List<string> names = [];
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}
