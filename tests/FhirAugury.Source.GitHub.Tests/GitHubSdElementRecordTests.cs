using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

public class GitHubSdElementRecordTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;

    public GitHubSdElementRecordTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sd_element_test_{Guid.NewGuid()}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Initialize_CreatesElementsTable()
    {
        using SqliteConnection conn = _db.OpenConnection();
        List<string> tables = GetTableNames(conn);
        Assert.Contains("github_sd_elements", tables);
    }

    [Fact]
    public void InsertAndSelect_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubStructureDefinitionRecord sd = CreateSdRecord("Patient");
        GitHubStructureDefinitionRecord.Insert(conn, sd);

        GitHubSdElementRecord record = new()
        {
            Id = GitHubSdElementRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            StructureDefinitionId = sd.Id,
            ElementId = "Patient.contact.relationship",
            Path = "Patient.contact.relationship",
            Name = "relationship",
            Short = "The kind of relationship",
            Definition = "The nature of the relationship between the patient and the contact person.",
            Comment = "Not all terminology uses fit this pattern.",
            MinCardinality = 0,
            MaxCardinality = "*",
            Types = "CodeableConcept",
            TypeProfiles = null,
            TargetProfiles = null,
            BindingStrength = "extensible",
            BindingValueSet = "http://hl7.org/fhir/ValueSet/patient-contactrelationship",
            SliceName = null,
            IsModifier = 0,
            IsSummary = 0,
            FixedValue = null,
            PatternValue = null,
            FieldOrder = 5,
        };

        GitHubSdElementRecord.Insert(conn, record);

        List<GitHubSdElementRecord> results = GitHubSdElementRecord.SelectList(conn);
        Assert.Single(results);

        GitHubSdElementRecord r = results[0];
        Assert.Equal("Patient.contact.relationship", r.ElementId);
        Assert.Equal("Patient.contact.relationship", r.Path);
        Assert.Equal("relationship", r.Name);
        Assert.Equal("The kind of relationship", r.Short);
        Assert.Equal("The nature of the relationship between the patient and the contact person.", r.Definition);
        Assert.Equal("Not all terminology uses fit this pattern.", r.Comment);
        Assert.Equal(0, r.MinCardinality);
        Assert.Equal("*", r.MaxCardinality);
        Assert.Equal("CodeableConcept", r.Types);
        Assert.Equal("extensible", r.BindingStrength);
        Assert.Equal("http://hl7.org/fhir/ValueSet/patient-contactrelationship", r.BindingValueSet);
        Assert.Equal(0, r.IsModifier);
        Assert.Equal(0, r.IsSummary);
        Assert.Equal(5, r.FieldOrder);
        Assert.Equal(sd.Id, r.StructureDefinitionId);
    }

    [Fact]
    public void SelectByStructureDefinitionId_ReturnsMatchingRecords()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubStructureDefinitionRecord sd1 = CreateSdRecord("Patient");
        GitHubStructureDefinitionRecord sd2 = CreateSdRecord("Observation", filePath: "obs.xml");
        GitHubStructureDefinitionRecord.Insert(conn, sd1);
        GitHubStructureDefinitionRecord.Insert(conn, sd2);

        InsertElement(conn, sd1.Id, "Patient", "Patient", 0);
        InsertElement(conn, sd1.Id, "Patient.name", "name", 1);
        InsertElement(conn, sd2.Id, "Observation", "Observation", 0);

        List<GitHubSdElementRecord> sd1Elements = GitHubSdElementRecord.SelectList(conn, StructureDefinitionId: sd1.Id);
        Assert.Equal(2, sd1Elements.Count);
        Assert.All(sd1Elements, e => Assert.Equal(sd1.Id, e.StructureDefinitionId));

        List<GitHubSdElementRecord> sd2Elements = GitHubSdElementRecord.SelectList(conn, StructureDefinitionId: sd2.Id);
        Assert.Single(sd2Elements);
    }

    [Fact]
    public void SelectByPath_ReturnsMatchingRecords()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubStructureDefinitionRecord sd = CreateSdRecord("Patient");
        GitHubStructureDefinitionRecord.Insert(conn, sd);

        InsertElement(conn, sd.Id, "Patient", "Patient", 0);
        InsertElement(conn, sd.Id, "Patient.name", "name", 1);
        InsertElement(conn, sd.Id, "Patient.birthDate", "birthDate", 2);

        List<GitHubSdElementRecord> results = GitHubSdElementRecord.SelectList(conn, Path: "Patient.name");
        Assert.Single(results);
        Assert.Equal("name", results[0].Name);
    }

    [Fact]
    public void SelectByBindingValueSet_ReturnsMatchingRecords()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubStructureDefinitionRecord sd = CreateSdRecord("Patient");
        GitHubStructureDefinitionRecord.Insert(conn, sd);

        GitHubSdElementRecord withBinding = new()
        {
            Id = GitHubSdElementRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            StructureDefinitionId = sd.Id,
            ElementId = "Patient.gender",
            Path = "Patient.gender",
            Name = "gender",
            BindingStrength = "required",
            BindingValueSet = "http://hl7.org/fhir/ValueSet/administrative-gender",
            FieldOrder = 0,
        };
        GitHubSdElementRecord.Insert(conn, withBinding);

        InsertElement(conn, sd.Id, "Patient.name", "name", 1);

        List<GitHubSdElementRecord> results = GitHubSdElementRecord.SelectList(
            conn, BindingValueSet: "http://hl7.org/fhir/ValueSet/administrative-gender");
        Assert.Single(results);
        Assert.Equal("gender", results[0].Name);
    }

    [Fact]
    public void Indexes_AreCreated()
    {
        using SqliteConnection conn = _db.OpenConnection();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'index' AND tbl_name = 'github_sd_elements'
            ORDER BY name
            """;
        using SqliteDataReader reader = cmd.ExecuteReader();

        List<string> indexNames = [];
        while (reader.Read())
        {
            indexNames.Add(reader.GetString(0));
        }

        Assert.NotEmpty(indexNames);
    }

    [Fact]
    public void ResetDatabase_DropsAndRecreatesTable()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubStructureDefinitionRecord sd = CreateSdRecord("Patient");
        GitHubStructureDefinitionRecord.Insert(conn, sd);
        InsertElement(conn, sd.Id, "Patient", "Patient", 0);
        Assert.Single(GitHubSdElementRecord.SelectList(conn));

        _db.ResetDatabase();

        using SqliteConnection conn2 = _db.OpenConnection();
        Assert.Empty(GitHubSdElementRecord.SelectList(conn2));
        Assert.Contains("github_sd_elements", GetTableNames(conn2));
    }

    [Fact]
    public void NullableFields_StoredCorrectly()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubStructureDefinitionRecord sd = CreateSdRecord("Patient");
        GitHubStructureDefinitionRecord.Insert(conn, sd);

        GitHubSdElementRecord record = new()
        {
            Id = GitHubSdElementRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            StructureDefinitionId = sd.Id,
            ElementId = "Patient",
            Path = "Patient",
            Name = "Patient",
            Short = null,
            Definition = null,
            Comment = null,
            MinCardinality = null,
            MaxCardinality = null,
            Types = null,
            TypeProfiles = null,
            TargetProfiles = null,
            BindingStrength = null,
            BindingValueSet = null,
            SliceName = null,
            IsModifier = null,
            IsSummary = null,
            FixedValue = null,
            PatternValue = null,
            FieldOrder = 0,
        };

        GitHubSdElementRecord.Insert(conn, record);

        List<GitHubSdElementRecord> results = GitHubSdElementRecord.SelectList(conn);
        Assert.Single(results);
        GitHubSdElementRecord r = results[0];
        Assert.Null(r.Short);
        Assert.Null(r.Definition);
        Assert.Null(r.MinCardinality);
        Assert.Null(r.Types);
        Assert.Null(r.BindingStrength);
        Assert.Null(r.IsModifier);
        Assert.Null(r.IsSummary);
    }

    private static void InsertElement(SqliteConnection conn, int sdId, string path, string name, int fieldOrder)
    {
        GitHubSdElementRecord record = new()
        {
            Id = GitHubSdElementRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            StructureDefinitionId = sdId,
            ElementId = path,
            Path = path,
            Name = name,
            FieldOrder = fieldOrder,
        };
        GitHubSdElementRecord.Insert(conn, record);
    }

    private static GitHubStructureDefinitionRecord CreateSdRecord(string name, string? filePath = null)
    {
        return new()
        {
            Id = GitHubStructureDefinitionRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            FilePath = filePath ?? $"source/{name.ToLowerInvariant()}/structuredefinition-{name.ToLowerInvariant()}.xml",
            Url = $"http://hl7.org/fhir/StructureDefinition/{name}",
            Name = name,
            ArtifactClass = "Resource",
            Kind = "resource",
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
