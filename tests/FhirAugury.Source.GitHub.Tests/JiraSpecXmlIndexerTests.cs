using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

public class JiraSpecXmlIndexerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly GitHubDatabase _database;
    private readonly JiraSpecXmlIndexer _indexer;
    private const string RepoName = "HL7/JIRA-Spec-Artifacts";

    public JiraSpecXmlIndexerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jiraspec-idx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        _database = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _database.Initialize();
        _indexer = new JiraSpecXmlIndexer(NullLogger<JiraSpecXmlIndexer>.Instance);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestFileCleanup.SafeDeleteDirectory(_tempDir);
    }

    private string CreateCloneDir()
    {
        string cloneDir = Path.Combine(_tempDir, "clone");
        Directory.CreateDirectory(Path.Combine(cloneDir, "xml"));
        return cloneDir;
    }

    private static void WriteFile(string dir, string relativePath, string content)
    {
        string fullPath = Path.Combine(dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private void SetupMinimalRepo(string cloneDir)
    {
        WriteFile(cloneDir, "xml/_families.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <families>
              <family key="FHIR"/>
              <family key="CDA"/>
              <family key="V2"/>
              <family key="OTHER"/>
            </families>
            """);

        WriteFile(cloneDir, "xml/SPECS-FHIR.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specifications>
              <specification key="core" name="FHIR Core"/>
              <specification key="us-core" name="US Core" deprecated="true"/>
            </specifications>
            """);
    }

    [Fact]
    public void ParseMinimalSpec_InsertsSpecRecord()
    {
        string cloneDir = CreateCloneDir();
        SetupMinimalRepo(cloneDir);
        WriteFile(cloneDir, "xml/FHIR-core.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specification key="core" url="http://hl7.org/fhir"
                           defaultWorkgroup="fhir-i" defaultVersion="STU4"
                           gitUrl="https://github.com/HL7/fhir"
                           ciUrl="http://build.fhir.org"
                           ballotUrl="http://hl7.org/fhir/2024Sep">
            </specification>
            """);

        using SqliteConnection connection = _database.OpenConnection();
        _indexer.IndexRepository(RepoName, cloneDir, connection, CancellationToken.None);

        List<JiraSpecRecord> specs = JiraSpecRecord.SelectList(connection, SpecKey: "core");
        Assert.Single(specs);
        JiraSpecRecord spec = specs[0];
        Assert.Equal("FHIR", spec.Family);
        Assert.Equal("core", spec.SpecKey);
        Assert.Equal("FHIR Core", spec.SpecName);
        Assert.Equal("http://hl7.org/fhir", spec.CanonicalUrl);
        Assert.Equal("fhir-i", spec.DefaultWorkgroup);
        Assert.Equal("STU4", spec.DefaultVersion);
        Assert.Equal("https://github.com/HL7/fhir", spec.GitUrl);
        Assert.Equal("http://build.fhir.org", spec.CiUrl);
        Assert.Equal("http://hl7.org/fhir/2024Sep", spec.BallotUrl);
    }

    [Fact]
    public void ParseVersions_IncludingDeprecated()
    {
        string cloneDir = CreateCloneDir();
        SetupMinimalRepo(cloneDir);
        WriteFile(cloneDir, "xml/FHIR-core.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specification key="core" defaultVersion="STU3">
              <version code="STU3" url="http://hl7.org/fhir/STU3"/>
              <version code="DSTU2" url="http://hl7.org/fhir/DSTU2" deprecated="true"/>
              <version code="STU4"/>
            </specification>
            """);

        using SqliteConnection connection = _database.OpenConnection();
        _indexer.IndexRepository(RepoName, cloneDir, connection, CancellationToken.None);

        List<JiraSpecVersionRecord> versions = JiraSpecVersionRecord.SelectList(connection, SpecKey: "core");
        Assert.Equal(3, versions.Count);
        Assert.Contains(versions, v => v.Code == "STU3" && !v.Deprecated && v.Url == "http://hl7.org/fhir/STU3");
        Assert.Contains(versions, v => v.Code == "DSTU2" && v.Deprecated);
        Assert.Contains(versions, v => v.Code == "STU4" && v.Url is null);
    }

    [Fact]
    public void ParseArtifacts_WithAndWithoutId()
    {
        string cloneDir = CreateCloneDir();
        SetupMinimalRepo(cloneDir);
        WriteFile(cloneDir, "xml/FHIR-core.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specification key="core" defaultVersion="STU3">
              <artifact key="patient" name="Patient" id="StructureDefinition/Patient"/>
              <artifact key="vitalsigns" name="Vital Signs Profile" deprecated="true"/>
            </specification>
            """);

        using SqliteConnection connection = _database.OpenConnection();
        _indexer.IndexRepository(RepoName, cloneDir, connection, CancellationToken.None);

        List<JiraSpecArtifactRecord> artifacts = JiraSpecArtifactRecord.SelectList(connection, SpecKey: "core");
        Assert.Equal(2, artifacts.Count);

        JiraSpecArtifactRecord patient = artifacts.First(a => a.ArtifactKey == "patient");
        Assert.Equal("Patient", patient.Name);
        Assert.Equal("StructureDefinition/Patient", patient.ArtifactId);
        Assert.Equal("StructureDefinition", patient.ResourceType);
        Assert.False(patient.Deprecated);

        JiraSpecArtifactRecord vitals = artifacts.First(a => a.ArtifactKey == "vitalsigns");
        Assert.Null(vitals.ArtifactId);
        Assert.Null(vitals.ResourceType);
        Assert.True(vitals.Deprecated);
    }

    [Fact]
    public void ParseArtifacts_WithOtherArtifact()
    {
        string cloneDir = CreateCloneDir();
        SetupMinimalRepo(cloneDir);
        WriteFile(cloneDir, "xml/FHIR-core.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specification key="core" defaultVersion="STU3">
              <artifact key="patient" name="Patient" id="StructureDefinition/Patient">
                <otherArtifact id="SearchParameter/Patient-name"/>
                <otherArtifact id="SearchParameter/Patient-birthdate"/>
              </artifact>
            </specification>
            """);

        using SqliteConnection connection = _database.OpenConnection();
        _indexer.IndexRepository(RepoName, cloneDir, connection, CancellationToken.None);

        List<JiraSpecArtifactRecord> artifacts = JiraSpecArtifactRecord.SelectList(connection, SpecKey: "core");
        Assert.Single(artifacts);
        Assert.NotNull(artifacts[0].OtherArtifactIds);
        Assert.Contains("SearchParameter/Patient-name", artifacts[0].OtherArtifactIds);
        Assert.Contains("SearchParameter/Patient-birthdate", artifacts[0].OtherArtifactIds);
    }

    [Fact]
    public void ParsePages_WithOtherPage()
    {
        string cloneDir = CreateCloneDir();
        SetupMinimalRepo(cloneDir);
        WriteFile(cloneDir, "xml/FHIR-core.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specification key="core" defaultVersion="STU3">
              <page key="index" name="Home Page" url="index.html" workgroup="fhir-i">
                <otherpage url="toc.html"/>
                <otherpage url="modules.html"/>
              </page>
            </specification>
            """);

        using SqliteConnection connection = _database.OpenConnection();
        _indexer.IndexRepository(RepoName, cloneDir, connection, CancellationToken.None);

        List<JiraSpecPageRecord> pages = JiraSpecPageRecord.SelectList(connection, SpecKey: "core");
        Assert.Single(pages);
        Assert.Equal("index", pages[0].PageKey);
        Assert.Equal("Home Page", pages[0].Name);
        Assert.Equal("index.html", pages[0].Url);
        Assert.Equal("fhir-i", pages[0].Workgroup);
        Assert.NotNull(pages[0].OtherPageUrls);
        Assert.Contains("toc.html", pages[0].OtherPageUrls);
        Assert.Contains("modules.html", pages[0].OtherPageUrls);
    }

    [Fact]
    public void ParseWorkgroups()
    {
        string cloneDir = CreateCloneDir();
        SetupMinimalRepo(cloneDir);
        WriteFile(cloneDir, "xml/_workgroups.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <workgroups>
              <workgroup key="fhir-i" name="FHIR Infrastructure" webcode="fhir" listserv="fhir@lists.hl7.org"/>
              <workgroup key="oo" name="Orders and Observations" deprecated="true"/>
            </workgroups>
            """);

        using SqliteConnection connection = _database.OpenConnection();
        _indexer.IndexRepository(RepoName, cloneDir, connection, CancellationToken.None);

        List<JiraWorkgroupRecord> workgroups = JiraWorkgroupRecord.SelectList(connection);
        Assert.Equal(2, workgroups.Count);
        Assert.Contains(workgroups, w => w.WorkgroupKey == "fhir-i" && w.Name == "FHIR Infrastructure" && !w.Deprecated);
        Assert.Contains(workgroups, w => w.WorkgroupKey == "oo" && w.Deprecated);
    }

    [Fact]
    public void ParseFamiliesAndSpecs()
    {
        string cloneDir = CreateCloneDir();
        SetupMinimalRepo(cloneDir);

        // Also add a CDA specs file
        WriteFile(cloneDir, "xml/SPECS-CDA.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specifications>
              <specification key="ccda" name="C-CDA"/>
            </specifications>
            """);

        using SqliteConnection connection = _database.OpenConnection();
        _indexer.IndexRepository(RepoName, cloneDir, connection, CancellationToken.None);

        List<JiraSpecFamilyRecord> families = JiraSpecFamilyRecord.SelectList(connection);
        Assert.Equal(3, families.Count); // core, us-core (from FHIR), ccda (from CDA)
        Assert.Contains(families, f => f.Family == "FHIR" && f.SpecKey == "core");
        Assert.Contains(families, f => f.Family == "FHIR" && f.SpecKey == "us-core" && f.Deprecated);
        Assert.Contains(families, f => f.Family == "CDA" && f.SpecKey == "ccda");
    }

    [Fact]
    public void ParseSpec_AllOptionalAttributesMissing()
    {
        string cloneDir = CreateCloneDir();
        SetupMinimalRepo(cloneDir);
        WriteFile(cloneDir, "xml/FHIR-core.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specification key="core">
            </specification>
            """);

        using SqliteConnection connection = _database.OpenConnection();
        _indexer.IndexRepository(RepoName, cloneDir, connection, CancellationToken.None);

        List<JiraSpecRecord> specs = JiraSpecRecord.SelectList(connection, SpecKey: "core");
        Assert.Single(specs);
        JiraSpecRecord spec = specs[0];
        Assert.Null(spec.CanonicalUrl);
        Assert.Null(spec.CiUrl);
        Assert.Null(spec.BallotUrl);
        Assert.Null(spec.GitUrl);
        Assert.Null(spec.DefaultWorkgroup);
        Assert.Equal("STU1", spec.DefaultVersion); // default
        Assert.Null(spec.ArtifactPageExtensions);
    }

    [Fact]
    public void ParseArtifactPageExtensions()
    {
        string cloneDir = CreateCloneDir();
        SetupMinimalRepo(cloneDir);
        WriteFile(cloneDir, "xml/FHIR-core.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specification key="core" defaultVersion="STU3">
              <artifactPageExtension value="-definitions"/>
              <artifactPageExtension value="-examples"/>
              <artifactPageExtension value="-mappings"/>
            </specification>
            """);

        using SqliteConnection connection = _database.OpenConnection();
        _indexer.IndexRepository(RepoName, cloneDir, connection, CancellationToken.None);

        List<JiraSpecRecord> specs = JiraSpecRecord.SelectList(connection, SpecKey: "core");
        Assert.Single(specs);
        Assert.NotNull(specs[0].ArtifactPageExtensions);
        Assert.Contains("-definitions", specs[0].ArtifactPageExtensions);
        Assert.Contains("-examples", specs[0].ArtifactPageExtensions);
        Assert.Contains("-mappings", specs[0].ArtifactPageExtensions);
    }

    [Fact]
    public void MalformedXml_SkipsFileAndContinues()
    {
        string cloneDir = CreateCloneDir();
        SetupMinimalRepo(cloneDir);

        // Write a malformed spec file
        WriteFile(cloneDir, "xml/FHIR-bad.xml", "<<<not valid xml>>>");

        // Write a valid spec file
        WriteFile(cloneDir, "xml/FHIR-good.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specification key="good" defaultVersion="STU1">
            </specification>
            """);

        using SqliteConnection connection = _database.OpenConnection();
        _indexer.IndexRepository(RepoName, cloneDir, connection, CancellationToken.None);

        // The good file should still be indexed
        List<JiraSpecRecord> specs = JiraSpecRecord.SelectList(connection, SpecKey: "good");
        Assert.Single(specs);
    }

    [Fact]
    public void MissingXmlDirectory_NoOp()
    {
        // Clone dir with no xml/ subdirectory and no families file
        string cloneDir = Path.Combine(_tempDir, "empty-clone");
        Directory.CreateDirectory(cloneDir);

        using SqliteConnection connection = _database.OpenConnection();
        _indexer.IndexRepository(RepoName, cloneDir, connection, CancellationToken.None);

        // No records should be created
        Assert.Empty(JiraSpecRecord.SelectList(connection));
        Assert.Empty(JiraWorkgroupRecord.SelectList(connection));
        Assert.Empty(JiraSpecFamilyRecord.SelectList(connection));
    }

    [Fact]
    public void ReIndexing_CleansUpOldData()
    {
        string cloneDir = CreateCloneDir();
        SetupMinimalRepo(cloneDir);
        WriteFile(cloneDir, "xml/FHIR-core.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specification key="core" defaultVersion="STU3">
              <artifact key="patient" name="Patient"/>
            </specification>
            """);

        using SqliteConnection connection = _database.OpenConnection();

        // First indexing
        _indexer.IndexRepository(RepoName, cloneDir, connection, CancellationToken.None);
        Assert.Single(JiraSpecArtifactRecord.SelectList(connection, SpecKey: "core"));

        // Change the spec file — remove the artifact
        WriteFile(cloneDir, "xml/FHIR-core.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specification key="core" defaultVersion="STU3">
            </specification>
            """);

        // Re-index
        _indexer.IndexRepository(RepoName, cloneDir, connection, CancellationToken.None);

        // Old artifact should be gone
        Assert.Empty(JiraSpecArtifactRecord.SelectList(connection, SpecKey: "core"));
        // Spec should still exist
        Assert.Single(JiraSpecRecord.SelectList(connection, SpecKey: "core"));
    }

    [Fact]
    public void RootLevelSpecFiles_AreIndexed()
    {
        string cloneDir = CreateCloneDir();
        SetupMinimalRepo(cloneDir);

        // Add OTHER family
        WriteFile(cloneDir, "xml/_families.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <families>
              <family key="FHIR"/>
              <family key="OTHER"/>
            </families>
            """);

        WriteFile(cloneDir, "xml/SPECS-OTHER.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specifications>
              <specification key="security" name="Security"/>
            </specifications>
            """);

        // Root-level OTHER-security.xml
        WriteFile(cloneDir, "OTHER-security.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <specification key="security" defaultVersion="STU1">
              <artifact key="sec-label" name="Security Label"/>
            </specification>
            """);

        using SqliteConnection connection = _database.OpenConnection();
        _indexer.IndexRepository(RepoName, cloneDir, connection, CancellationToken.None);

        List<JiraSpecRecord> specs = JiraSpecRecord.SelectList(connection, SpecKey: "security");
        Assert.Single(specs);
        Assert.Equal("OTHER", specs[0].Family);
        Assert.Single(JiraSpecArtifactRecord.SelectList(connection, SpecKey: "security"));
    }
}
