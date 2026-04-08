using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using FhirAugury.Source.GitHub.Ingestion.Categories;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

public class CanonicalArtifactIndexerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;
    private readonly CanonicalArtifactIndexer _indexer;
    private readonly string _testDataDir;

    public CanonicalArtifactIndexerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ca_test_{Guid.NewGuid()}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
        _indexer = new CanonicalArtifactIndexer(_db, NullLogger<CanonicalArtifactIndexer>.Instance);
        _testDataDir = Path.Combine(Path.GetTempPath(), $"ca_testdata_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_testDataDir, true); } catch { }
    }

    [Fact]
    public void DatabaseSchema_CreatesCanonicalArtifactsTable()
    {
        using SqliteConnection conn = _db.OpenConnection();
        List<string> tables = GetTableNames(conn);

        Assert.Contains("github_canonical_artifacts", tables);
    }

    [Fact]
    public void DatabaseSchema_CreatesFtsTable()
    {
        using SqliteConnection conn = _db.OpenConnection();
        List<string> tables = GetTableNames(conn);

        Assert.Contains("github_canonical_artifacts_fts", tables);
    }

    [Fact]
    public void InsertAndSelect_CanonicalArtifactRecord_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();

        GitHubCanonicalArtifactRecord record = new GitHubCanonicalArtifactRecord
        {
            Id = GitHubCanonicalArtifactRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            FilePath = "source/patient/codesystem-example.xml",
            ResourceType = "CodeSystem",
            Url = "http://hl7.org/fhir/example",
            Name = "Example",
            Title = "Example Code System",
            Version = "5.0.0",
            Status = "active",
            Description = "An example code system",
            Publisher = "HL7",
            WorkGroup = "fhir-i",
            FhirMaturity = 5,
            StandardsStatus = "normative",
            TypeSpecificData = """{"content":"complete","conceptCount":3}""",
            Format = "xml",
        };

        GitHubCanonicalArtifactRecord.Insert(conn, record);
        List<GitHubCanonicalArtifactRecord> results = GitHubCanonicalArtifactRecord.SelectList(conn, RepoFullName: "HL7/fhir");

        Assert.Single(results);
        Assert.Equal("CodeSystem", results[0].ResourceType);
        Assert.Equal("http://hl7.org/fhir/example", results[0].Url);
        Assert.Equal("Example", results[0].Name);
        Assert.Equal(5, results[0].FhirMaturity);
    }

    [Fact]
    public void IndexFiles_ParsesCodeSystemXml()
    {
        string repoDir = Path.Combine(_testDataDir, "repo");
        Directory.CreateDirectory(repoDir);

        string xmlContent = """
            <?xml version="1.0" encoding="UTF-8"?>
            <CodeSystem xmlns="http://hl7.org/fhir">
              <url value="http://hl7.org/fhir/test-codesystem"/>
              <name value="TestCodeSystem"/>
              <title value="Test Code System"/>
              <status value="active"/>
              <description value="A test code system"/>
              <content value="complete"/>
            </CodeSystem>
            """;

        string filePath = Path.Combine(repoDir, "codesystem-test.xml");
        File.WriteAllText(filePath, xmlContent);

        int count = _indexer.IndexFiles("test/repo", repoDir, [filePath], CancellationToken.None);

        Assert.Equal(1, count);

        using SqliteConnection conn = _db.OpenConnection();
        List<GitHubCanonicalArtifactRecord> records = GitHubCanonicalArtifactRecord.SelectList(conn, RepoFullName: "test/repo");

        Assert.Single(records);
        Assert.Equal("CodeSystem", records[0].ResourceType);
        Assert.Equal("http://hl7.org/fhir/test-codesystem", records[0].Url);
        Assert.Equal("TestCodeSystem", records[0].Name);
        Assert.Equal("xml", records[0].Format);
    }

    [Fact]
    public void IndexFiles_ParsesValueSetXml()
    {
        string repoDir = Path.Combine(_testDataDir, "repo2");
        Directory.CreateDirectory(repoDir);

        string xmlContent = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ValueSet xmlns="http://hl7.org/fhir">
              <url value="http://hl7.org/fhir/test-valueset"/>
              <name value="TestValueSet"/>
              <title value="Test Value Set"/>
              <status value="draft"/>
              <description value="A test value set"/>
            </ValueSet>
            """;

        string filePath = Path.Combine(repoDir, "valueset-test.xml");
        File.WriteAllText(filePath, xmlContent);

        int count = _indexer.IndexFiles("test/repo", repoDir, [filePath], CancellationToken.None);

        Assert.Equal(1, count);

        using SqliteConnection conn = _db.OpenConnection();
        List<GitHubCanonicalArtifactRecord> records = GitHubCanonicalArtifactRecord.SelectList(conn, RepoFullName: "test/repo");

        Assert.Single(records);
        Assert.Equal("ValueSet", records[0].ResourceType);
        Assert.Equal("draft", records[0].Status);
    }

    [Fact]
    public void IndexFiles_ParsesSearchParameterBundle()
    {
        string repoDir = Path.Combine(_testDataDir, "repo3");
        Directory.CreateDirectory(repoDir);

        string xmlContent = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Bundle xmlns="http://hl7.org/fhir">
              <type value="collection"/>
              <entry>
                <resource>
                  <SearchParameter>
                    <url value="http://hl7.org/fhir/SearchParameter/Patient-name"/>
                    <name value="name"/>
                    <status value="active"/>
                    <code value="name"/>
                    <base value="Patient"/>
                    <type value="string"/>
                    <expression value="Patient.name"/>
                  </SearchParameter>
                </resource>
              </entry>
              <entry>
                <resource>
                  <SearchParameter>
                    <url value="http://hl7.org/fhir/SearchParameter/Patient-birthdate"/>
                    <name value="birthdate"/>
                    <status value="active"/>
                    <code value="birthdate"/>
                    <base value="Patient"/>
                    <type value="date"/>
                    <expression value="Patient.birthDate"/>
                  </SearchParameter>
                </resource>
              </entry>
            </Bundle>
            """;

        string filePath = Path.Combine(repoDir, "bundle-patient-search-params.xml");
        File.WriteAllText(filePath, xmlContent);

        int count = _indexer.IndexFiles("test/repo", repoDir, [filePath], CancellationToken.None);

        Assert.Equal(2, count);

        using SqliteConnection conn = _db.OpenConnection();
        List<GitHubCanonicalArtifactRecord> records = GitHubCanonicalArtifactRecord.SelectList(conn, RepoFullName: "test/repo");

        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal("SearchParameter", r.ResourceType));
    }

    [Fact]
    public void IndexFiles_SkipsNonCanonicalResources()
    {
        string repoDir = Path.Combine(_testDataDir, "repo4");
        Directory.CreateDirectory(repoDir);

        string xmlContent = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Patient xmlns="http://hl7.org/fhir">
              <id value="example"/>
              <name>
                <family value="Doe"/>
              </name>
            </Patient>
            """;

        string filePath = Path.Combine(repoDir, "patient-example.xml");
        File.WriteAllText(filePath, xmlContent);

        int count = _indexer.IndexFiles("test/repo", repoDir, [filePath], CancellationToken.None);

        Assert.Equal(0, count);
    }

    [Fact]
    public void IndexFiles_HandlesInvalidXmlGracefully()
    {
        string repoDir = Path.Combine(_testDataDir, "repo5");
        Directory.CreateDirectory(repoDir);

        string filePath = Path.Combine(repoDir, "broken.xml");
        File.WriteAllText(filePath, "this is not valid xml <<<<>>");

        int count = _indexer.IndexFiles("test/repo", repoDir, [filePath], CancellationToken.None);

        Assert.Equal(0, count);
    }

    [Fact]
    public void IndexFiles_ClearsExistingRecordsBeforeReindexing()
    {
        string repoDir = Path.Combine(_testDataDir, "repo6");
        Directory.CreateDirectory(repoDir);

        string xmlContent = """
            <?xml version="1.0" encoding="UTF-8"?>
            <CodeSystem xmlns="http://hl7.org/fhir">
              <url value="http://hl7.org/fhir/cs-one"/>
              <name value="CsOne"/>
              <status value="active"/>
              <content value="complete"/>
            </CodeSystem>
            """;

        string filePath = Path.Combine(repoDir, "codesystem-one.xml");
        File.WriteAllText(filePath, xmlContent);

        // Index once
        _indexer.IndexFiles("test/repo", repoDir, [filePath], CancellationToken.None);

        // Index again — should replace, not duplicate
        int count = _indexer.IndexFiles("test/repo", repoDir, [filePath], CancellationToken.None);

        using SqliteConnection conn = _db.OpenConnection();
        List<GitHubCanonicalArtifactRecord> records = GitHubCanonicalArtifactRecord.SelectList(conn, RepoFullName: "test/repo");

        Assert.Equal(1, records.Count);
    }

    [Fact]
    public void IndexFiles_ParsesJsonFormat()
    {
        string repoDir = Path.Combine(_testDataDir, "repo7");
        Directory.CreateDirectory(repoDir);

        string jsonContent = """
            {
              "resourceType": "CodeSystem",
              "url": "http://hl7.org/fhir/json-codesystem",
              "name": "JsonCodeSystem",
              "title": "JSON Code System",
              "status": "active",
              "content": "complete"
            }
            """;

        string filePath = Path.Combine(repoDir, "codesystem-json.json");
        File.WriteAllText(filePath, jsonContent);

        int count = _indexer.IndexFiles("test/repo", repoDir, [filePath], CancellationToken.None);

        Assert.Equal(1, count);

        using SqliteConnection conn = _db.OpenConnection();
        List<GitHubCanonicalArtifactRecord> records = GitHubCanonicalArtifactRecord.SelectList(conn, RepoFullName: "test/repo");

        Assert.Single(records);
        Assert.Equal("json", records[0].Format);
        Assert.Equal("JsonCodeSystem", records[0].Name);
    }

    [Fact]
    public void FhirCoreStrategy_DiscoverCanonicalArtifactFiles_FindsArtifacts()
    {
        // Create FHIR Core-like structure
        string cloneDir = Path.Combine(_testDataDir, "fhir-core");
        string sourceDir = Path.Combine(cloneDir, "source");
        string patientDir = Path.Combine(sourceDir, "patient");
        Directory.CreateDirectory(patientDir);

        // Create fhir.ini for validation
        File.WriteAllText(Path.Combine(sourceDir, "fhir.ini"), "[patient]\ncategory=resource\n");

        // Canonical artifacts
        File.WriteAllText(Path.Combine(patientDir, "codesystem-example.xml"), "<x/>");
        File.WriteAllText(Path.Combine(patientDir, "valueset-example.xml"), "<x/>");
        File.WriteAllText(Path.Combine(patientDir, "searchparameter-example.xml"), "<x/>");
        File.WriteAllText(Path.Combine(patientDir, "bundle-patient-search-params.xml"), "<x/>");
        // Non-canonical file (should be skipped)
        File.WriteAllText(Path.Combine(patientDir, "patient-spreadsheet.xml"), "<x/>");

        FhirCoreStrategy strategy = new(NullLogger<FhirCoreStrategy>.Instance);
        IReadOnlyList<string> files = strategy.DiscoverCanonicalArtifactFiles("HL7/fhir", cloneDir, CancellationToken.None);

        Assert.Equal(4, files.Count);
    }

    [Fact]
    public void UtgStrategy_DiscoverCanonicalArtifactFiles_FindsXmlFiles()
    {
        string cloneDir = Path.Combine(_testDataDir, "utg");
        string sourceDir = Path.Combine(cloneDir, "input", "sourceOfTruth", "fhir");
        Directory.CreateDirectory(sourceDir);

        File.WriteAllText(Path.Combine(sourceDir, "cs-v3-ActCode.xml"), "<x/>");
        File.WriteAllText(Path.Combine(sourceDir, "vs-v3-ActCode.xml"), "<x/>");

        UtgStrategy strategy = new(null!, NullLogger<UtgStrategy>.Instance);
        IReadOnlyList<string> files = strategy.DiscoverCanonicalArtifactFiles("HL7/UTG", cloneDir, CancellationToken.None);

        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void FhirExtensionsPackStrategy_DiscoverCanonicalArtifactFiles_FiltersByPrefix()
    {
        string cloneDir = Path.Combine(_testDataDir, "extensions");
        string defsDir = Path.Combine(cloneDir, "input", "definitions");
        Directory.CreateDirectory(defsDir);

        File.WriteAllText(Path.Combine(defsDir, "CodeSystem-example.xml"), "<x/>");
        File.WriteAllText(Path.Combine(defsDir, "ValueSet-example.xml"), "<x/>");
        File.WriteAllText(Path.Combine(defsDir, "SearchParameter-example.xml"), "<x/>");
        // Non-matching file
        File.WriteAllText(Path.Combine(defsDir, "StructureDefinition-example.xml"), "<x/>");

        FhirExtensionsPackStrategy strategy = new(null!, NullLogger<FhirExtensionsPackStrategy>.Instance);
        IReadOnlyList<string> files = strategy.DiscoverCanonicalArtifactFiles("HL7/fhir-extensions", cloneDir, CancellationToken.None);

        Assert.Equal(3, files.Count);
    }

    [Fact]
    public void IncubatorStrategy_DiscoverCanonicalArtifactFiles_FindsXmlAndJson()
    {
        string cloneDir = Path.Combine(_testDataDir, "incubator");
        string resourcesDir = Path.Combine(cloneDir, "input", "resources");
        Directory.CreateDirectory(resourcesDir);

        File.WriteAllText(Path.Combine(resourcesDir, "ValueSet-example.xml"), "<x/>");
        File.WriteAllText(Path.Combine(resourcesDir, "CodeSystem-example.json"), "{}");

        IncubatorStrategy strategy = new(null!, NullLogger<IncubatorStrategy>.Instance);
        IReadOnlyList<string> files = strategy.DiscoverCanonicalArtifactFiles("HL7/some-incubator", cloneDir, CancellationToken.None);

        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void IgStrategy_DiscoverCanonicalArtifactFiles_FindsXmlAndJson()
    {
        string cloneDir = Path.Combine(_testDataDir, "ig");
        string resourcesDir = Path.Combine(cloneDir, "input", "resources");
        Directory.CreateDirectory(resourcesDir);

        File.WriteAllText(Path.Combine(resourcesDir, "ValueSet-example.xml"), "<x/>");
        File.WriteAllText(Path.Combine(resourcesDir, "CodeSystem-example.json"), "{}");
        File.WriteAllText(Path.Combine(resourcesDir, "SearchParameter-example.xml"), "<x/>");

        IgStrategy strategy = new(null!, NullLogger<IgStrategy>.Instance);
        IReadOnlyList<string> files = strategy.DiscoverCanonicalArtifactFiles("HL7/us-core", cloneDir, CancellationToken.None);

        Assert.Equal(3, files.Count);
    }

    [Fact]
    public void Strategy_EmptyDirectory_ReturnsEmptyList()
    {
        string cloneDir = Path.Combine(_testDataDir, "empty");
        Directory.CreateDirectory(cloneDir);

        FhirCoreStrategy fhirCore = new(NullLogger<FhirCoreStrategy>.Instance);
        UtgStrategy utg = new(null!, NullLogger<UtgStrategy>.Instance);
        FhirExtensionsPackStrategy extensions = new(null!, NullLogger<FhirExtensionsPackStrategy>.Instance);

        Assert.Empty(fhirCore.DiscoverCanonicalArtifactFiles("repo", cloneDir, CancellationToken.None));
        Assert.Empty(utg.DiscoverCanonicalArtifactFiles("repo", cloneDir, CancellationToken.None));
        Assert.Empty(extensions.DiscoverCanonicalArtifactFiles("repo", cloneDir, CancellationToken.None));
    }

    [Fact]
    public void ResetDatabase_DropsAndRecreatesCanonicalArtifactsTable()
    {
        using SqliteConnection conn = _db.OpenConnection();

        // Insert a record
        GitHubCanonicalArtifactRecord.Insert(conn, new GitHubCanonicalArtifactRecord
        {
            Id = GitHubCanonicalArtifactRecord.GetIndex(),
            RepoFullName = "test/repo",
            FilePath = "test.xml",
            ResourceType = "CodeSystem",
            Url = "http://example.com/cs",
            Name = "Test",
            Format = "xml",
        });

        // Reset
        _db.ResetDatabase();

        using SqliteConnection conn2 = _db.OpenConnection();
        List<GitHubCanonicalArtifactRecord> records = GitHubCanonicalArtifactRecord.SelectList(conn2);
        Assert.Empty(records);

        // Table should still exist
        List<string> tables = GetTableNames(conn2);
        Assert.Contains("github_canonical_artifacts", tables);
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
