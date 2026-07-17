using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Tools.FhirSpecReview.Readers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.FhirSpecReview.Tests;

/// <summary>
/// Exercises the Phase-3 readers against seeded mini DBs, a fixture clone tree,
/// a fixture fhir-spec.db, dictionary.db, and a baseline-site folder. All raw
/// connections use <c>;Pooling=False</c>; temp dirs are removed via
/// <see cref="TestFileCleanup"/>.
/// </summary>
public sealed class ReadersTests : IDisposable
{
    private const string Repo = "HL7/fhir";
    private readonly string _tempDir;

    public ReadersTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "readers-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    // ---- GitHub cache reader ------------------------------------------------

    [Fact]
    public void GitHubCacheReader_Vocabulary_Pages_Artifacts_RawRead_And_Guard()
    {
        string cacheDir = Path.Combine(_tempDir, "cache");
        string cloneRoot = Path.Combine(cacheDir, "github", "repos", "HL7_fhir", "clone");
        Directory.CreateDirectory(Path.Combine(cloneRoot, "source", "patient"));

        File.WriteAllText(Path.Combine(cloneRoot, "publish.ini"), """
            [pages]
            foo.html =
            ;commented.html =
            [page-titles]
            foo.html = Foo Page
            """);
        File.WriteAllText(Path.Combine(cloneRoot, "source", "foo.html"), "<html><body><p>hello</p></body></html>");
        File.WriteAllText(Path.Combine(cloneRoot, "source", "patient", "structuredefinition-Patient.xml"), "<StructureDefinition/>");
        File.WriteAllText(Path.Combine(cloneRoot, "source", "patient", "patient-introduction.xml"), "<div>intro</div>");

        string dbPath = Path.Combine(_tempDir, "github.db");
        SeedGitHubDb(dbPath);

        using GitHubCacheReader reader = new(dbPath, cacheDir, Repo, NullLogger.Instance);

        Assert.True(reader.CloneRootExists);

        SpecVocabulary vocab = reader.LoadCurrentVocabulary();
        Assert.Equal("Resource", vocab.Structures["patient"]);
        Assert.Contains("patientcontact", (IEnumerable<string>)vocab.ElementPaths);
        Assert.Contains("identifier", (IEnumerable<string>)vocab.SearchParameterNames);

        List<NarrativePageInfo> pages = reader.EnumerateNarrativePages();
        NarrativePageInfo foo = Assert.Single(pages);
        Assert.Equal("foo.html", foo.PageFileName);
        Assert.Equal("Foo Page", foo.Label);
        Assert.True(foo.ExistsInPublishIni);
        Assert.True(foo.ExistsInSource);

        List<ArtifactInfo> artifacts = reader.EnumerateArtifacts();
        ArtifactInfo patient = Assert.Single(artifacts, a => a.Name == "Patient");
        Assert.Equal("resource", patient.ArtifactType);
        Assert.True(patient.SourceDirectoryExists);
        Assert.Equal("patient-introduction.xml", patient.IntroPageFilename);
        Assert.Null(patient.NotesPageFilename);

        Assert.Contains("hello", reader.ReadRawMarkup("source/foo.html"));
        Assert.Null(reader.ReadRawMarkup("../../../../etc/passwd"));
    }

    [Fact]
    public void EnumerateArtifacts_WorkGroup_FallsBack_To_Wg_Extension_In_DefinitionXml()
    {
        string cacheDir = Path.Combine(_tempDir, "cache-wgext");
        string cloneRoot = Path.Combine(cacheDir, "github", "repos", "HL7_fhir", "clone");
        Directory.CreateDirectory(Path.Combine(cloneRoot, "source", "observation"));

        File.WriteAllText(
            Path.Combine(cloneRoot, "source", "observation", "structuredefinition-Observation.xml"),
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <StructureDefinition xmlns="http://hl7.org/fhir">
              <extension url="http://hl7.org/fhir/StructureDefinition/structuredefinition-wg">
                <valueCode value="oo"/>
              </extension>
              <url value="http://hl7.org/fhir/StructureDefinition/Observation"/>
            </StructureDefinition>
            """);

        string dbPath = Path.Combine(_tempDir, "github-wgext.db");
        SeedArtifactWgDb(dbPath,
            filePath: "source/observation/structuredefinition-Observation.xml",
            url: "http://hl7.org/fhir/StructureDefinition/Observation",
            name: "Observation",
            workGroup: null,
            workGroupRaw: null);

        using GitHubCacheReader reader = new(dbPath, cacheDir, Repo, NullLogger.Instance);
        ArtifactInfo observation = Assert.Single(reader.EnumerateArtifacts());
        Assert.Equal("oo", observation.WorkGroupCode);
        Assert.Equal("Orders and Observations", observation.WorkGroupName);
    }

    [Fact]
    public void EnumerateArtifacts_WorkGroup_Uses_WorkGroupRaw_Without_Reading_File()
    {
        string cacheDir = Path.Combine(_tempDir, "cache-wgraw");
        string cloneRoot = Path.Combine(cacheDir, "github", "repos", "HL7_fhir", "clone");
        Directory.CreateDirectory(Path.Combine(cloneRoot, "source"));

        string dbPath = Path.Combine(_tempDir, "github-wgraw.db");
        SeedArtifactWgDb(dbPath,
            filePath: "source/account/structuredefinition-Account.xml",
            url: "http://hl7.org/fhir/StructureDefinition/Account",
            name: "Account",
            workGroup: null,
            workGroupRaw: "pa");

        using GitHubCacheReader reader = new(dbPath, cacheDir, Repo, NullLogger.Instance);
        ArtifactInfo account = Assert.Single(reader.EnumerateArtifacts());
        Assert.Equal("pa", account.WorkGroupCode);
        Assert.Equal("Patient Administration", account.WorkGroupName);
    }

    private static void SeedArtifactWgDb(
        string dbPath, string filePath, string url, string name, string? workGroup, string? workGroupRaw)
    {
        using (GitHubDatabase db = new(dbPath, NullLogger<GitHubDatabase>.Instance))
        {
            db.Initialize();
        }

        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        conn.Open();
        conn.Insert(new GitHubStructureDefinitionRecord
        {
            Id = GitHubStructureDefinitionRecord.GetIndex(),
            RepoFullName = Repo,
            FilePath = filePath,
            Url = url,
            Name = name,
            ArtifactClass = "Resource",
            Kind = "resource",
            Status = "active",
            WorkGroup = workGroup,
            WorkGroupRaw = workGroupRaw,
            FhirMaturity = 5,
            StandardsStatus = "trial-use",
        }, insertPrimaryKey: true);
    }

    private static void SeedGitHubDb(string dbPath)
    {
        using (GitHubDatabase db = new(dbPath, NullLogger<GitHubDatabase>.Instance))
        {
            db.Initialize();
        }

        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        conn.Open();

        conn.Insert(new GitHubStructureDefinitionRecord
        {
            Id = GitHubStructureDefinitionRecord.GetIndex(),
            RepoFullName = Repo,
            FilePath = "source/patient/structuredefinition-Patient.xml",
            Url = "http://hl7.org/fhir/StructureDefinition/Patient",
            Name = "Patient",
            ArtifactClass = "Resource",
            Kind = "resource",
            Status = "active",
            WorkGroup = "fhir",
            FhirMaturity = 5,
            StandardsStatus = "trial-use",
        }, insertPrimaryKey: true);

        conn.Insert(new GitHubSdElementRecord
        {
            Id = GitHubSdElementRecord.GetIndex(),
            RepoFullName = Repo,
            StructureDefinitionId = 1,
            ElementId = "Patient.contact",
            Path = "Patient.contact",
            Name = "contact",
            FieldOrder = 0,
        }, insertPrimaryKey: true);

        conn.Insert(new GitHubCanonicalArtifactRecord
        {
            Id = GitHubCanonicalArtifactRecord.GetIndex(),
            RepoFullName = Repo,
            FilePath = "source/patient/searchparameter-identifier.xml",
            ResourceType = "SearchParameter",
            Url = "http://hl7.org/fhir/SearchParameter/identifier",
            Name = "identifier",
            Format = "xml",
        }, insertPrimaryKey: true);

        conn.Insert(new Hl7WorkGroupRecord
        {
            Id = Hl7WorkGroupRecord.GetIndex(),
            Code = "fhir",
            Name = "FHIR Infrastructure",
            Definition = null,
            Retired = false,
            NameClean = "fhirinfrastructure",
        }, insertPrimaryKey: true);
    }

    // ---- fhir-spec.db reader ------------------------------------------------

    [Fact]
    public void FhirSpecDbReader_Resolves_Release_And_Loads_Vocabulary()
    {
        string dbPath = Path.Combine(_tempDir, "fhir-spec.db");
        SeedFhirSpecDb(dbPath);

        FhirSpecDbReader reader = new(dbPath);

        int? byShort = reader.ResolvePackageKey("R5", out string? err1);
        Assert.Null(err1);
        Assert.Equal(5, byShort);

        int? byVersion = reader.ResolvePackageKey("5.0", out string? err2);
        Assert.Null(err2);
        Assert.Equal(5, byVersion);

        int? unknown = reader.ResolvePackageKey("R9", out string? err3);
        Assert.Null(unknown);
        Assert.NotNull(err3);
        Assert.Contains("R5", err3);

        SpecVocabulary vocab = reader.LoadBaselineVocabulary(5);
        Assert.Equal("Resource", vocab.Structures["account"]);
        Assert.Contains("accountstatus", (IEnumerable<string>)vocab.ElementPaths);
        Assert.Contains("patient", (IEnumerable<string>)vocab.SearchParameterNames);
    }

    private static void SeedFhirSpecDb(string dbPath)
    {
        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        conn.Open();
        Exec(conn, """
            CREATE TABLE Packages (Key INTEGER PRIMARY KEY, Name TEXT, PackageId TEXT, FhirVersionShort TEXT, ShortName TEXT);
            CREATE TABLE Structures (PackageKey INTEGER, Name TEXT, ArtifactClass TEXT);
            CREATE TABLE Elements (PackageKey INTEGER, Path TEXT);
            CREATE TABLE SearchParameters (PackageKey INTEGER, Name TEXT);
            INSERT INTO Packages VALUES (5, 'hl7.fhir.r5.core', 'hl7.fhir.r5.core', '5.0', 'R5');
            INSERT INTO Structures VALUES (5, 'Account', 'Resource');
            INSERT INTO Elements VALUES (5, 'Account.status');
            INSERT INTO SearchParameters VALUES (5, 'patient');
            """);
    }

    // ---- dictionary.db reader ----------------------------------------------

    [Fact]
    public void DictionaryReader_Loads_Words_And_Typos()
    {
        string dbPath = Path.Combine(_tempDir, "dictionary.db");
        using (SqliteConnection conn = new($"Data Source={dbPath};Pooling=False"))
        {
            conn.Open();
            Exec(conn, """
                CREATE TABLE words (Word TEXT);
                CREATE TABLE typos (Typo TEXT, Correction TEXT);
                INSERT INTO words VALUES ('Patient');
                INSERT INTO typos VALUES ('abandonned', 'abandoned');
                """);
        }

        DictionaryReader reader = new(dbPath);
        Assert.True(reader.Exists);
        DictionaryData data = reader.Load();
        Assert.Contains("patient", (IEnumerable<string>)data.Words);
        Assert.Equal("abandoned", data.Typos["abandonned"]);
    }

    // ---- baseline-site reader ----------------------------------------------

    [Fact]
    public void BaselineSiteReader_Builds_Presence_Sets()
    {
        string siteDir = Path.Combine(_tempDir, "site");
        Directory.CreateDirectory(Path.Combine(siteDir, "patient"));
        File.WriteAllText(Path.Combine(siteDir, "datatypes.html"), "<html/>");

        BaselineSiteReader reader = new(siteDir);
        Assert.True(reader.Exists);
        BaselinePresence presence = reader.Load();
        Assert.Contains("patient", (IEnumerable<string>)presence.SanitizedEntities);
        Assert.Contains("datatypes", (IEnumerable<string>)presence.SanitizedEntities);
        Assert.Contains("datatypes.html", (IEnumerable<string>)presence.PageFileNames);
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
