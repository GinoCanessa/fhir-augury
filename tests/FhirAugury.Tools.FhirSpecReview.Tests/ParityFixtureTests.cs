using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.FhirSpecReview.Tests;

/// <summary>
/// Parity fixture: a representative artifact with intro/notes pages. Asserts
/// artifact inventory + intro/notes discovery (getExpectedLocations port),
/// artifact-page review (conformance counts), removed-artifact detection, and
/// that a real current-build structure/element is not flagged. Guards against
/// sanitizer / enumeration drift.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class ParityFixtureTests : IDisposable
{
    private const string Repo = "HL7/fhir";
    private readonly string _tempDir;

    public ParityFixtureTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "parity-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    [Fact]
    public async Task Artifact_Intro_Notes_Discovery_And_Review()
    {
        string cacheDir = Path.Combine(_tempDir, "cache");
        string cloneRoot = Path.Combine(cacheDir, "github", "repos", "HL7_fhir", "clone");
        string patientDir = Path.Combine(cloneRoot, "source", "patient");
        Directory.CreateDirectory(patientDir);

        File.WriteAllText(Path.Combine(cloneRoot, "publish.ini"), """
            [FHIR]
            version = 6.0.0-test
            [pages]
            """);
        File.WriteAllText(Path.Combine(patientDir, "structuredefinition-Patient.xml"), "<StructureDefinition/>");
        File.WriteAllText(Path.Combine(patientDir, "patient-introduction.xml"),
            "<div>The Patient resource SHALL define identity using Patient.contact. Conformance was removed.</div>");
        File.WriteAllText(Path.Combine(patientDir, "patient-notes.xml"),
            "<div>Implementers SHOULD review notes.</div>");

        string githubDb = Path.Combine(_tempDir, "github.db");
        SeedGitHubDb(githubDb);

        string fhirSpecDb = Path.Combine(_tempDir, "fhir-spec.db");
        SeedFhirSpecDb(fhirSpecDb);

        string dictDb = Path.Combine(_tempDir, "dictionary.db");
        using (SqliteConnection conn = new($"Data Source={dictDb};Pooling=False"))
        {
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE words (Word TEXT);
                CREATE TABLE typos (Typo TEXT, Correction TEXT);
                INSERT INTO words VALUES ('the');
                """;
            cmd.ExecuteNonQuery();
        }

        string siteDir = Path.Combine(_tempDir, "site");
        Directory.CreateDirectory(siteDir);

        string reviewDb = Path.Combine(_tempDir, "review.db");

        ProcessOptions options = new(
            GitHubDbPath: githubDb,
            GitHubCachePath: cacheDir,
            Repo: Repo,
            FhirSpecDbPath: fhirSpecDb,
            BaselineRelease: "R5",
            BaselineSitePath: siteDir,
            DictionaryDbPath: dictDb,
            ReviewDbPath: reviewDb,
            FhirR6DbPath: Path.Combine(_tempDir, "no-r6.db"),
            DropTables: true);

        int exit = await RunRedirectedAsync(options);
        Assert.Equal(0, exit);

        using SqliteConnection db = new($"Data Source={reviewDb};Pooling=False");
        db.Open();

        // artifact inventory + intro/notes discovery
        long artifactId = (long)Scalar(db, "SELECT Id FROM artifacts WHERE FhirId='Patient'")!;
        Assert.Equal("patient-introduction.xml", (string?)Scalar(db, "SELECT IntroPageFilename FROM artifacts WHERE Id=$id", artifactId));
        Assert.Equal("patient-notes.xml", (string?)Scalar(db, "SELECT NotesPageFilename FROM artifacts WHERE Id=$id", artifactId));
        Assert.Equal(1L, ScalarLong(db, "SELECT SourceDirectoryExists FROM artifacts WHERE Id=$id", artifactId));
        Assert.Equal(1L, ScalarLong(db, "SELECT SourceDefinitionExists FROM artifacts WHERE Id=$id", artifactId));

        // intro page reviewed (linked by ArtifactId), conformance counted
        long introId = (long)Scalar(db, "SELECT Id FROM pages WHERE ArtifactId=$id AND PageFileName='patient-introduction.xml'", artifactId)!;
        Assert.Equal(1L, ScalarLong(db, "SELECT ConformantShallCount FROM pages WHERE Id=$id", introId));

        // removed-artifact detection on the intro page
        Assert.Equal(1L, ScalarLong(db, "SELECT COUNT(*) FROM page_removed_fhir_artifacts WHERE PageId=$id AND Word='Conformance'", introId));

        // sanitizer guard: real structure/element not flagged
        Assert.Equal(0L, ScalarLong(db, "SELECT COUNT(*) FROM page_unknown_words WHERE PageId=$id AND Word IN ('Patient','Patient.contact','Patient.contact.')", introId));
        Assert.Equal(0L, ScalarLong(db, "SELECT COUNT(*) FROM page_removed_fhir_artifacts WHERE PageId=$id AND Word IN ('Patient','Patient.contact','Patient.contact.')", introId));

        // notes page reviewed
        Assert.Equal(1L, ScalarLong(db, "SELECT COUNT(*) FROM pages WHERE ArtifactId=$id AND PageFileName='patient-notes.xml'", artifactId));
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
            WorkGroup = "pa",
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
    }

    private static void SeedFhirSpecDb(string dbPath)
    {
        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE Packages (Key INTEGER PRIMARY KEY, Name TEXT, PackageId TEXT, FhirVersionShort TEXT, ShortName TEXT);
            CREATE TABLE Structures (PackageKey INTEGER, Name TEXT, ArtifactClass TEXT);
            CREATE TABLE Elements (PackageKey INTEGER, Path TEXT);
            CREATE TABLE SearchParameters (PackageKey INTEGER, Name TEXT);
            INSERT INTO Packages VALUES (5, 'hl7.fhir.r5.core', 'hl7.fhir.r5.core', '5.0', 'R5');
            INSERT INTO Structures VALUES (5, 'Conformance', 'Resource');
            """;
        cmd.ExecuteNonQuery();
    }

    private static async Task<int> RunRedirectedAsync(ProcessOptions options)
    {
        TextWriter origOut = Console.Out;
        TextWriter origErr = Console.Error;
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            return await ProcessRunner.RunAsync(options, ConsoleLogger.Instance).ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    private static object? Scalar(SqliteConnection conn, string sql, long? id = null)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (id is not null) cmd.Parameters.AddWithValue("$id", id.Value);
        object? result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static long ScalarLong(SqliteConnection conn, string sql, long? id = null)
    {
        object? result = Scalar(conn, sql, id);
        return result is null ? 0 : Convert.ToInt64(result);
    }
}
