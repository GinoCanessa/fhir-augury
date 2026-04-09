using FhirAugury.Parsing.Fsh;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

public class FshArtifactIndexerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;
    private readonly FshArtifactIndexer _indexer;
    private readonly string _cloneDir;

    public FshArtifactIndexerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"fsh_indexer_test_{Guid.NewGuid()}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
        _indexer = new FshArtifactIndexer(_db, NullLogger<FshArtifactIndexer>.Instance);
        _cloneDir = Path.Combine(Path.GetTempPath(), $"fsh_indexer_clone_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_cloneDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_cloneDir, true); } catch { }
    }

    private string CreateFshFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(_cloneDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    // ── CodeSystem indexing ──────────────────────────────────────────

    [Fact]
    public void IndexFshFiles_CodeSystem_InsertsCanonicalArtifact()
    {
        string fshContent = """
            CodeSystem: TestCS
            Id: test-cs
            Title: "Test Code System"
            Description: "A test code system"
            * #code1 "Code One"
            """;

        string fshFile = CreateFshFile("input/fsh/TestCS.fsh", fshContent);
        SushiConfig config = new("test.ig", "http://example.org/test", "TestIG", "Test IG", "4.0.1", "draft", [], []);

        int count = _indexer.IndexFshFiles("test/repo", _cloneDir, [fshFile], config, CancellationToken.None);

        Assert.Equal(1, count);

        using SqliteConnection conn = _db.OpenConnection();
        List<GitHubCanonicalArtifactRecord> records = GitHubCanonicalArtifactRecord.SelectList(conn, RepoFullName: "test/repo");

        Assert.Single(records);
        Assert.Equal("CodeSystem", records[0].ResourceType);
        Assert.Equal("http://example.org/test/CodeSystem/test-cs", records[0].Url);
        Assert.Equal("TestCS", records[0].Name);
        Assert.Equal("fsh", records[0].Format);
    }

    // ── ValueSet indexing ────────────────────────────────────────────

    [Fact]
    public void IndexFshFiles_ValueSet_InsertsCanonicalArtifact()
    {
        string fshContent = """
            ValueSet: TestVS
            Id: test-vs
            Title: "Test Value Set"
            Description: "A test value set"
            """;

        string fshFile = CreateFshFile("input/fsh/TestVS.fsh", fshContent);
        SushiConfig config = new("test.ig", "http://example.org/test", "TestIG", "Test IG", "4.0.1", "draft", [], []);

        int count = _indexer.IndexFshFiles("test/repo", _cloneDir, [fshFile], config, CancellationToken.None);

        Assert.Equal(1, count);

        using SqliteConnection conn = _db.OpenConnection();
        List<GitHubCanonicalArtifactRecord> records = GitHubCanonicalArtifactRecord.SelectList(conn, RepoFullName: "test/repo");

        Assert.Single(records);
        Assert.Equal("ValueSet", records[0].ResourceType);
        Assert.Equal("http://example.org/test/ValueSet/test-vs", records[0].Url);
        Assert.Equal("fsh", records[0].Format);
    }

    // ── Profile indexing ─────────────────────────────────────────────

    [Fact]
    public void IndexFshFiles_Profile_InsertsStructureDefinition()
    {
        string fshContent = """
            Profile: TestPatient
            Parent: Patient
            Id: test-patient
            Title: "Test Patient Profile"
            Description: "A test patient profile"
            """;

        string fshFile = CreateFshFile("input/fsh/TestPatient.fsh", fshContent);
        SushiConfig config = new("test.ig", "http://example.org/test", "TestIG", "Test IG", "4.0.1", "draft", [], []);

        int count = _indexer.IndexFshFiles("test/repo", _cloneDir, [fshFile], config, CancellationToken.None);

        Assert.Equal(1, count);

        using SqliteConnection conn = _db.OpenConnection();
        List<GitHubStructureDefinitionRecord> records = GitHubStructureDefinitionRecord.SelectList(conn, RepoFullName: "test/repo");

        Assert.Single(records);
        Assert.Equal("http://example.org/test/StructureDefinition/test-patient", records[0].Url);
        Assert.Equal("TestPatient", records[0].Name);
        Assert.Equal("Profile", records[0].ArtifactClass);
        Assert.Equal("resource", records[0].Kind);
        Assert.Equal("constraint", records[0].Derivation);
    }

    // ── Extension indexing ───────────────────────────────────────────

    [Fact]
    public void IndexFshFiles_Extension_InsertsStructureDefinition()
    {
        string fshContent = """
            Extension: TestExtension
            Id: test-extension
            Title: "Test Extension"
            Description: "A test extension"
            * value[x] only string
            """;

        string fshFile = CreateFshFile("input/fsh/TestExtension.fsh", fshContent);
        SushiConfig config = new("test.ig", "http://example.org/test", "TestIG", "Test IG", "4.0.1", "draft", [], []);

        int count = _indexer.IndexFshFiles("test/repo", _cloneDir, [fshFile], config, CancellationToken.None);

        Assert.Equal(1, count);

        using SqliteConnection conn = _db.OpenConnection();
        List<GitHubStructureDefinitionRecord> records = GitHubStructureDefinitionRecord.SelectList(conn, RepoFullName: "test/repo");

        Assert.Single(records);
        Assert.Equal("Extension", records[0].ArtifactClass);
        Assert.Equal("complex-type", records[0].Kind);
    }

    // ── Multiple definitions in one file ─────────────────────────────

    [Fact]
    public void IndexFshFiles_MultipleDefinitions_IndexesAll()
    {
        string fshContent = """
            Profile: TestPatient
            Parent: Patient
            Id: test-patient
            Title: "Test Patient"

            CodeSystem: TestCS
            Id: test-cs
            Title: "Test CS"
            * #code1 "Code 1"

            ValueSet: TestVS
            Id: test-vs
            Title: "Test VS"
            """;

        string fshFile = CreateFshFile("input/fsh/Multiple.fsh", fshContent);
        SushiConfig config = new("test.ig", "http://example.org/test", "TestIG", "Test IG", "4.0.1", "draft", [], []);

        int count = _indexer.IndexFshFiles("test/repo", _cloneDir, [fshFile], config, CancellationToken.None);

        Assert.Equal(3, count);

        using SqliteConnection conn = _db.OpenConnection();
        List<GitHubCanonicalArtifactRecord> artifacts = GitHubCanonicalArtifactRecord.SelectList(conn, RepoFullName: "test/repo");
        List<GitHubStructureDefinitionRecord> sds = GitHubStructureDefinitionRecord.SelectList(conn, RepoFullName: "test/repo");

        Assert.Equal(2, artifacts.Count); // CodeSystem + ValueSet
        Assert.Single(sds);              // Profile
    }

    // ── No sushi-config → urn:unknown URLs ───────────────────────────

    [Fact]
    public void IndexFshFiles_NoSushiConfig_UsesUrnUnknownUrls()
    {
        string fshContent = """
            CodeSystem: TestCS
            Id: test-cs
            Title: "Test CS"
            * #code1 "Code 1"
            """;

        string fshFile = CreateFshFile("input/fsh/TestCS.fsh", fshContent);

        int count = _indexer.IndexFshFiles("test/repo", _cloneDir, [fshFile], null, CancellationToken.None);

        Assert.Equal(1, count);

        using SqliteConnection conn = _db.OpenConnection();
        List<GitHubCanonicalArtifactRecord> records = GitHubCanonicalArtifactRecord.SelectList(conn, RepoFullName: "test/repo");

        Assert.Single(records);
        Assert.Equal("urn:unknown:TestCS", records[0].Url);
    }

    // ── Empty FSH file → no records ──────────────────────────────────

    [Fact]
    public void IndexFshFiles_EmptyFile_ReturnsZero()
    {
        string fshFile = CreateFshFile("input/fsh/empty.fsh", "// empty file");

        SushiConfig config = new("test.ig", "http://example.org/test", "TestIG", "Test IG", "4.0.1", "draft", [], []);
        int count = _indexer.IndexFshFiles("test/repo", _cloneDir, [fshFile], config, CancellationToken.None);

        Assert.Equal(0, count);
    }

    // ── Alias-only file → no records ─────────────────────────────────

    [Fact]
    public void IndexFshFiles_AliasOnly_ReturnsZero()
    {
        string fshContent = """
            Alias: $SCT = http://snomed.info/sct
            Alias: $LOINC = http://loinc.org
            """;

        string fshFile = CreateFshFile("input/fsh/aliases.fsh", fshContent);
        SushiConfig config = new("test.ig", "http://example.org/test", "TestIG", "Test IG", "4.0.1", "draft", [], []);

        int count = _indexer.IndexFshFiles("test/repo", _cloneDir, [fshFile], config, CancellationToken.None);

        Assert.Equal(0, count);
    }

    // ── Empty file list → no records ─────────────────────────────────

    [Fact]
    public void IndexFshFiles_EmptyFileList_ReturnsZero()
    {
        SushiConfig config = new("test.ig", "http://example.org/test", "TestIG", "Test IG", "4.0.1", "draft", [], []);
        int count = _indexer.IndexFshFiles("test/repo", _cloneDir, [], config, CancellationToken.None);

        Assert.Equal(0, count);
    }

    // ── Relative path in records uses forward slashes ─────────────────

    [Fact]
    public void IndexFshFiles_RelativePath_UsesForwardSlashes()
    {
        string fshContent = """
            CodeSystem: DeepCS
            Id: deep-cs
            Title: "Deep CS"
            * #code1 "Code"
            """;

        string fshFile = CreateFshFile("input/fsh/nested/deep/DeepCS.fsh", fshContent);
        SushiConfig config = new("test.ig", "http://example.org/test", "TestIG", "Test IG", "4.0.1", "draft", [], []);

        _indexer.IndexFshFiles("test/repo", _cloneDir, [fshFile], config, CancellationToken.None);

        using SqliteConnection conn = _db.OpenConnection();
        List<GitHubCanonicalArtifactRecord> records = GitHubCanonicalArtifactRecord.SelectList(conn, RepoFullName: "test/repo");

        Assert.Single(records);
        Assert.Equal("input/fsh/nested/deep/DeepCS.fsh", records[0].FilePath);
    }

    // ── Cancellation is respected ────────────────────────────────────

    [Fact]
    public void IndexFshFiles_CancellationRequested_Throws()
    {
        string fshContent = """
            CodeSystem: TestCS
            Id: test-cs
            * #code1 "Code"
            """;

        string fshFile = CreateFshFile("input/fsh/TestCS.fsh", fshContent);
        SushiConfig config = new("test.ig", "http://example.org/test", "TestIG", "Test IG", "4.0.1", "draft", [], []);

        CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            _indexer.IndexFshFiles("test/repo", _cloneDir, [fshFile], config, cts.Token));
    }

    // ── DefinitionalInstance → canonical artifact ─────────────────────

    [Fact]
    public void IndexFshFiles_DefinitionalInstance_InsertsCanonicalArtifact()
    {
        string fshContent = """
            Instance: TestOperation
            InstanceOf: OperationDefinition
            Usage: #definition
            Title: "Test Operation"
            Description: "A test operation"
            * name = "test-operation"
            """;

        string fshFile = CreateFshFile("input/fsh/TestOp.fsh", fshContent);
        SushiConfig config = new("test.ig", "http://example.org/test", "TestIG", "Test IG", "4.0.1", "draft", [], []);

        int count = _indexer.IndexFshFiles("test/repo", _cloneDir, [fshFile], config, CancellationToken.None);

        Assert.Equal(1, count);

        using SqliteConnection conn = _db.OpenConnection();
        List<GitHubCanonicalArtifactRecord> records = GitHubCanonicalArtifactRecord.SelectList(conn, RepoFullName: "test/repo");

        Assert.Single(records);
        Assert.Equal("OperationDefinition", records[0].ResourceType);
        Assert.Equal("fsh", records[0].Format);
    }

    // ── Non-definitional Instance → skipped ──────────────────────────

    [Fact]
    public void IndexFshFiles_ExampleInstance_IsSkipped()
    {
        string fshContent = """
            Instance: ExamplePatient
            InstanceOf: Patient
            Usage: #example
            * name.family = "Smith"
            """;

        string fshFile = CreateFshFile("input/fsh/ExamplePatient.fsh", fshContent);
        SushiConfig config = new("test.ig", "http://example.org/test", "TestIG", "Test IG", "4.0.1", "draft", [], []);

        int count = _indexer.IndexFshFiles("test/repo", _cloneDir, [fshFile], config, CancellationToken.None);

        Assert.Equal(0, count);
    }

    // ── Logical model ────────────────────────────────────────────────

    [Fact]
    public void IndexFshFiles_Logical_InsertsStructureDefinition()
    {
        string fshContent = """
            Logical: TestModel
            Id: test-model
            Title: "Test Logical Model"
            * name 1..1 string "Name"
            """;

        string fshFile = CreateFshFile("input/fsh/TestModel.fsh", fshContent);
        SushiConfig config = new("test.ig", "http://example.org/test", "TestIG", "Test IG", "4.0.1", "draft", [], []);

        int count = _indexer.IndexFshFiles("test/repo", _cloneDir, [fshFile], config, CancellationToken.None);

        Assert.Equal(1, count);

        using SqliteConnection conn = _db.OpenConnection();
        List<GitHubStructureDefinitionRecord> records = GitHubStructureDefinitionRecord.SelectList(conn, RepoFullName: "test/repo");

        Assert.Single(records);
        Assert.Equal("LogicalModel", records[0].ArtifactClass);
        Assert.Equal("logical", records[0].Kind);
    }

    // ── Multiple FSH files ───────────────────────────────────────────

    [Fact]
    public void IndexFshFiles_MultipleFiles_IndexesAllFiles()
    {
        string file1Content = """
            Profile: ProfileA
            Parent: Patient
            Id: profile-a
            Title: "Profile A"
            """;

        string file2Content = """
            CodeSystem: CodeSystemB
            Id: codesystem-b
            Title: "CodeSystem B"
            * #b1 "B1"
            """;

        string file1 = CreateFshFile("input/fsh/ProfileA.fsh", file1Content);
        string file2 = CreateFshFile("input/fsh/CodeSystemB.fsh", file2Content);
        SushiConfig config = new("test.ig", "http://example.org/test", "TestIG", "Test IG", "4.0.1", "draft", [], []);

        int count = _indexer.IndexFshFiles("test/repo", _cloneDir, [file1, file2], config, CancellationToken.None);

        Assert.Equal(2, count);
    }
}
