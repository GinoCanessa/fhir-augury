using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.FhirSpecReview.Tests;

/// <summary>
/// End-to-end <c>process</c> over a seeded GitHub DB + clone tree, fhir-spec.db,
/// dictionary.db, and a baseline-site folder. Asserts each content check fires,
/// the removed-vs-unknown split, baseline-presence tracking, and that a known
/// current-build structure/element is NOT falsely flagged (sanitizer guard).
/// Redirects the console, so joins the shared ConsoleRedirect collection.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class ContentReviewTests : IDisposable
{
    private const string Repo = "HL7/fhir";
    private readonly string _tempDir;

    public ContentReviewTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "review-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    [Fact]
    public async Task Process_Produces_All_Check_Results()
    {
        // ---- clone tree ----
        string cacheDir = Path.Combine(_tempDir, "cache");
        string cloneRoot = Path.Combine(cacheDir, "github", "repos", "HL7_fhir", "clone");
        Directory.CreateDirectory(Path.Combine(cloneRoot, "source"));

        File.WriteAllText(Path.Combine(cloneRoot, "publish.ini"), """
            [FHIR]
            version = 6.0.0-test
            [pages]
            foo.html =
            [page-titles]
            foo.html = Foo Page
            """);

        File.WriteAllText(Path.Combine(cloneRoot, "source", "foo.html"), """
            <html><body>
            <table class="colstu"><tr>
            <td id="wg"><a href="[%wg fhir%]">[%wgt fhir%]</a> Work Group</td>
            <td id="fmm"><a href="x">Maturity Level</a>: 3</td>
            <td id="ballot"><a href="x">Standards Status</a>: Trial Use</td>
            </tr></table>
            <p>The system SHALL support Patient and Patient.contact. The system shall also work.</p>
            <p>This references Conformance which was removed. See dstu2 for history.</p>
            <p>Zorblax is unknown and abandonned is a typo.</p>
            <p>TODO finish this. See https://chat.fhir.org/topic for discussion.</p>
            <p><img src="diagram.png"></p>
            <p>[%stu-note%] reviewers please check.</p>
            </body></html>
            """);

        // ---- github source db ----
        string githubDb = Path.Combine(_tempDir, "github.db");
        SeedGitHubDb(githubDb);

        // ---- fhir-spec.db (baseline) ----
        string fhirSpecDb = Path.Combine(_tempDir, "fhir-spec.db");
        SeedFhirSpecDb(fhirSpecDb);

        // ---- dictionary.db ----
        string dictDb = Path.Combine(_tempDir, "dictionary.db");
        using (SqliteConnection conn = new($"Data Source={dictDb};Pooling=False"))
        {
            conn.Open();
            Exec(conn, """
                CREATE TABLE words (Word TEXT);
                CREATE TABLE typos (Typo TEXT, Correction TEXT);
                INSERT INTO words VALUES ('system');
                INSERT INTO typos VALUES ('abandonned', 'abandoned');
                """);
        }

        // ---- baseline site ----
        string siteDir = Path.Combine(_tempDir, "site");
        Directory.CreateDirectory(Path.Combine(siteDir, "olddir"));
        File.WriteAllText(Path.Combine(siteDir, "removedpage.html"), "<html/>");

        // ---- fhir-r6.db (artifact inventory) ----
        string fhirR6Db = Path.Combine(_tempDir, "fhir-r6.db");
        SeedFhirR6Db(fhirR6Db);

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
            FhirR6DbPath: fhirR6Db,
            DropTables: true);

        int exit = await RunRedirectedAsync(options);
        Assert.Equal(0, exit);

        using SqliteConnection db = new($"Data Source={reviewDb};Pooling=False");
        db.Open();

        long pageId = (long)Scalar(db, "SELECT Id FROM pages WHERE PageFileName='foo.html'")!;
        Assert.Equal(1L, ScalarLong(db, "SELECT ConformantShallCount FROM pages WHERE Id=$id", pageId));
        Assert.Equal(1L, ScalarLong(db, "SELECT NonConformantShallCount FROM pages WHERE Id=$id", pageId));
        Assert.Equal("fhir", (string?)Scalar(db, "SELECT ResponsibleWorkGroupCode FROM pages WHERE Id=$id", pageId));
        Assert.True(ScalarLong(db, "SELECT ZulipLinkCount FROM pages WHERE Id=$id", pageId) >= 1);
        Assert.True(ScalarLong(db, "SELECT PriorFhirVersionReferenceCount FROM pages WHERE Id=$id", pageId) >= 1);
        Assert.True(ScalarLong(db, "SELECT ImagesWithIssuesCount FROM pages WHERE Id=$id", pageId) >= 1);

        string? markers = (string?)Scalar(db, "SELECT PossibleIncompleteMarkers FROM pages WHERE Id=$id", pageId);
        Assert.Contains("TODO", markers, StringComparison.OrdinalIgnoreCase);
        string? notes = (string?)Scalar(db, "SELECT ReaderReviewNotes FROM pages WHERE Id=$id", pageId);
        Assert.Contains("stu-note", notes, StringComparison.OrdinalIgnoreCase);

        // unknown + typo split
        Assert.Equal(1L, ScalarLong(db, "SELECT COUNT(*) FROM page_unknown_words WHERE PageId=$id AND Word='Zorblax' AND IsTypo=0", pageId));
        Assert.Equal(1L, ScalarLong(db, "SELECT COUNT(*) FROM page_unknown_words WHERE PageId=$id AND Word='abandonned' AND IsTypo=1", pageId));
        Assert.Equal("abandoned", (string?)Scalar(db, "SELECT Correction FROM page_unknown_words WHERE PageId=$id AND Word='abandonned'", pageId));

        // sanitizer regression guard: a real current-build structure/element must NOT be flagged
        Assert.Equal(0L, ScalarLong(db, "SELECT COUNT(*) FROM page_unknown_words WHERE PageId=$id AND Word IN ('Patient','Patient.contact','Patient.contact.')", pageId));
        Assert.Equal(0L, ScalarLong(db, "SELECT COUNT(*) FROM page_removed_fhir_artifacts WHERE PageId=$id AND Word IN ('Patient','Patient.contact','Patient.contact.')", pageId));

        // removed FHIR artifact (baseline-only token)
        Assert.Equal(1L, ScalarLong(db, "SELECT COUNT(*) FROM page_removed_fhir_artifacts WHERE PageId=$id AND Word='Conformance'", pageId));

        // image issue row
        Assert.Equal(1L, ScalarLong(db, "SELECT COUNT(*) FROM page_images WHERE PageId=$id AND Source='diagram.png' AND MissingAlt=1", pageId));

        // Phase 2: finding pointers + context snippets
        Assert.Equal("source/foo.html", (string?)Scalar(db, "SELECT SourceRelativePath FROM pages WHERE Id=$id", pageId));

        string? removedSnippet = (string?)Scalar(db, "SELECT ContextSnippet FROM page_removed_fhir_artifacts WHERE PageId=$id AND Word='Conformance'", pageId);
        Assert.False(string.IsNullOrEmpty(removedSnippet));
        Assert.Contains("Conformance", removedSnippet);

        string? unknownSnippet = (string?)Scalar(db, "SELECT ContextSnippet FROM page_unknown_words WHERE PageId=$id AND Word='Zorblax'", pageId);
        Assert.False(string.IsNullOrEmpty(unknownSnippet));
        Assert.Contains("Zorblax", unknownSnippet);

        string? imageSnippet = (string?)Scalar(db, "SELECT ContextSnippet FROM page_images WHERE PageId=$id AND Source='diagram.png'", pageId);
        Assert.False(string.IsNullOrEmpty(imageSnippet));
        Assert.Contains("diagram.png", imageSnippet);

        // baseline-presence removed entities
        Assert.Equal(1L, ScalarLong(db, "SELECT COUNT(*) FROM removed_baseline_entities WHERE EntityKind='page' AND Name='removedpage.html'"));
        Assert.Equal(1L, ScalarLong(db, "SELECT COUNT(*) FROM removed_baseline_entities WHERE EntityKind='artifact' AND Name='olddir'"));

        // provenance
        Assert.Equal(1L, ScalarLong(db, "SELECT COUNT(*) FROM review_runs"));
        Assert.Equal("6.0.0-test", (string?)Scalar(db, "SELECT BuildVersion FROM review_runs LIMIT 1"));

        // Phase 3: artifact inventory populated from fhir-r6.db for Patient.
        long artifactId = (long)Scalar(db, "SELECT Id FROM artifacts WHERE FhirId='Patient'")!;
        Assert.Equal(1L, ScalarLong(db, "SELECT COUNT(*) FROM artifact_elements WHERE ArtifactId=$id AND Path='Patient.gender'", artifactId));
        Assert.Equal(1L, ScalarLong(db, "SELECT COUNT(*) FROM artifact_operations WHERE ArtifactId=$id AND OperationId='Patient-match'", artifactId));
        Assert.Equal(1L, ScalarLong(db, "SELECT COUNT(*) FROM artifact_search_parameters WHERE ArtifactId=$id AND SearchParamId='Patient-active'", artifactId));
    }

    [Fact]
    public async Task Process_With_Missing_FhirR6Db_Succeeds_With_Empty_Inventory()
    {
        string cacheDir = Path.Combine(_tempDir, "cache");
        string cloneRoot = Path.Combine(cacheDir, "github", "repos", "HL7_fhir", "clone");
        Directory.CreateDirectory(Path.Combine(cloneRoot, "source"));
        File.WriteAllText(Path.Combine(cloneRoot, "publish.ini"), """
            [FHIR]
            version = 6.0.0-test
            [pages]
            [page-titles]
            """);

        string githubDb = Path.Combine(_tempDir, "github.db");
        SeedGitHubDb(githubDb);

        string fhirSpecDb = Path.Combine(_tempDir, "fhir-spec.db");
        SeedFhirSpecDb(fhirSpecDb);

        string dictDb = Path.Combine(_tempDir, "dictionary.db");
        using (SqliteConnection conn = new($"Data Source={dictDb};Pooling=False"))
        {
            conn.Open();
            Exec(conn, "CREATE TABLE words (Word TEXT); CREATE TABLE typos (Typo TEXT, Correction TEXT);");
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
            FhirR6DbPath: Path.Combine(_tempDir, "does-not-exist.db"),
            DropTables: true);

        int exit = await RunRedirectedAsync(options);
        Assert.Equal(0, exit);

        using SqliteConnection db = new($"Data Source={reviewDb};Pooling=False");
        db.Open();
        Assert.Equal(0L, ScalarLong(db, "SELECT COUNT(*) FROM artifact_elements"));
        Assert.Equal(0L, ScalarLong(db, "SELECT COUNT(*) FROM artifact_operations"));
        Assert.Equal(0L, ScalarLong(db, "SELECT COUNT(*) FROM artifact_search_parameters"));
    }

    [Fact]
    public async Task Process_With_Duplicate_Canonical_Url_Skips_Duplicate_And_Records_Finding()
    {
        // ---- minimal clone tree (publish.ini + empty source) ----
        string cacheDir = Path.Combine(_tempDir, "cache");
        string cloneRoot = Path.Combine(cacheDir, "github", "repos", "HL7_fhir", "clone");
        Directory.CreateDirectory(Path.Combine(cloneRoot, "source"));

        File.WriteAllText(Path.Combine(cloneRoot, "publish.ini"), """
            [FHIR]
            version = 6.0.0-test
            [pages]
            [page-titles]
            """);

        // ---- github source db: two extensions sharing one canonical URL ----
        const string sharedUrl = "http://hl7.org/fhir/StructureDefinition/operationoutcome-issue-source";
        string githubDb = Path.Combine(_tempDir, "github.db");
        using (GitHubDatabase db = new(githubDb, NullLogger<GitHubDatabase>.Instance))
        {
            db.Initialize();
        }
        using (SqliteConnection conn = new($"Data Source={githubDb};Pooling=False"))
        {
            conn.Open();
            conn.Insert(new GitHubStructureDefinitionRecord
            {
                Id = GitHubStructureDefinitionRecord.GetIndex(),
                RepoFullName = Repo,
                FilePath = "source/operationoutcome/structuredefinition-OOSourceFile.xml",
                Url = sharedUrl,
                Name = "OOSourceFile",
                ArtifactClass = "Extension",
                Kind = "complex-type",
                Status = "active",
                WorkGroup = "fhir",
            }, insertPrimaryKey: true);
            conn.Insert(new GitHubStructureDefinitionRecord
            {
                Id = GitHubStructureDefinitionRecord.GetIndex(),
                RepoFullName = Repo,
                FilePath = "source/operationoutcome/structuredefinition-OOIssueCol.xml",
                Url = sharedUrl,
                Name = "OOIssueCol",
                ArtifactClass = "Extension",
                Kind = "complex-type",
                Status = "active",
                WorkGroup = "fhir",
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

        // ---- fhir-spec.db / dictionary.db / baseline site ----
        string fhirSpecDb = Path.Combine(_tempDir, "fhir-spec.db");
        SeedFhirSpecDb(fhirSpecDb);

        string dictDb = Path.Combine(_tempDir, "dictionary.db");
        using (SqliteConnection conn = new($"Data Source={dictDb};Pooling=False"))
        {
            conn.Open();
            Exec(conn, "CREATE TABLE words (Word TEXT); CREATE TABLE typos (Typo TEXT, Correction TEXT);");
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

        // Must not throw despite the colliding (RepoFullName, FhirId).
        int exit = await RunRedirectedAsync(options);
        Assert.Equal(0, exit);

        using SqliteConnection rdb = new($"Data Source={reviewDb};Pooling=False");
        rdb.Open();

        // Exactly one artifacts row for the colliding FhirId.
        Assert.Equal(1L, ScalarLong(rdb,
            "SELECT COUNT(*) FROM artifacts WHERE FhirId='operationoutcome-issue-source'"));

        // Exactly one finding, with the deterministically-kept/skipped names + URLs.
        Assert.Equal(1L, ScalarLong(rdb, "SELECT COUNT(*) FROM duplicate_artifact_keys"));
        Assert.Equal("OOIssueCol", (string?)Scalar(rdb,
            "SELECT KeptName FROM duplicate_artifact_keys WHERE FhirId='operationoutcome-issue-source'"));
        Assert.Equal("OOSourceFile", (string?)Scalar(rdb,
            "SELECT DuplicateName FROM duplicate_artifact_keys WHERE FhirId='operationoutcome-issue-source'"));
        Assert.Equal(sharedUrl, (string?)Scalar(rdb,
            "SELECT KeptCanonicalUrl FROM duplicate_artifact_keys WHERE FhirId='operationoutcome-issue-source'"));
        Assert.Equal(sharedUrl, (string?)Scalar(rdb,
            "SELECT DuplicateCanonicalUrl FROM duplicate_artifact_keys WHERE FhirId='operationoutcome-issue-source'"));
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
            INSERT INTO Structures VALUES (5, 'Conformance', 'Resource');
            INSERT INTO Elements VALUES (5, 'Account.status');
            INSERT INTO SearchParameters VALUES (5, 'identifier');
            """);
    }

    private static void SeedFhirR6Db(string dbPath)
    {
        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        conn.Open();
        Exec(conn, """
            CREATE TABLE Packages (Key INTEGER PRIMARY KEY, Name TEXT, PackageId TEXT, FhirVersionShort TEXT, ShortName TEXT);
            CREATE TABLE Structures (Key INTEGER PRIMARY KEY, PackageKey INTEGER, Id TEXT, Name TEXT);
            CREATE TABLE Elements (
                PackageKey INTEGER, Key INTEGER PRIMARY KEY, StructureKey INTEGER, ResourceFieldOrder INTEGER,
                Path TEXT, MinCardinality INTEGER, MaxCardinalityString TEXT, StandardStatus TEXT,
                FixedValue TEXT, PatternValue TEXT, ValueSetBindingStrength TEXT, BindingValueSet TEXT,
                MeaningWhenMissing TEXT, IsModifier INTEGER);
            CREATE TABLE Operations (
                Key INTEGER PRIMARY KEY, PackageKey INTEGER, Id TEXT, Code TEXT, Name TEXT, Kind TEXT,
                Status TEXT, StandardStatus TEXT, FhirMaturity INTEGER, IsExperimental INTEGER,
                WorkGroup TEXT, Description TEXT, ResourceTypes TEXT, AdditionalResourceTypes TEXT);
            CREATE TABLE SearchParameters (
                Key INTEGER PRIMARY KEY, PackageKey INTEGER, Id TEXT, Name TEXT, Status TEXT,
                FhirMaturity INTEGER, StandardStatus TEXT, IsExperimental INTEGER, WorkGroup TEXT,
                SearchType TEXT, Description TEXT, BaseResources TEXT, AdditionalBaseResources TEXT);

            INSERT INTO Packages VALUES (1, 'hl7.fhir.r6.core', 'hl7.fhir.r6.core', '6.0', 'R6');
            INSERT INTO Structures VALUES (10, 1, 'Patient', 'Patient');
            INSERT INTO Elements VALUES
                (1, 100, 10, 0, 'Patient.gender', 1, '1', '', NULL, NULL, 'Required', 'http://hl7.org/fhir/ValueSet/administrative-gender', NULL, 0);
            INSERT INTO Operations VALUES
                (200, 1, 'Patient-match', 'match', 'Patient Match', 'operation', 'active', 'trial-use', 2, 0, 'pa', 'Match a patient', 'Patient', NULL);
            INSERT INTO SearchParameters VALUES
                (300, 1, 'Patient-active', 'active', 'active', 3, 'normative', 0, 'pa', 'token', 'Active flag', 'Patient', NULL);
            """);
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
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
