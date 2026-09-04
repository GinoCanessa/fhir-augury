using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.BallotNotesReallocateWg.Tests;

/// <summary>
/// Exercises <see cref="ReallocateRunner"/> end-to-end against a throwaway git
/// clone, a seeded notes DB, and minimal reference DBs. A <c>Page</c> note whose
/// stored WG is stale resolves via the clone's <c>[%wg%]</c> marker to a different
/// owner, so dry-run / write / idempotency / guard behavior is observable.
/// </summary>
public sealed class ReallocateRunnerTests : IDisposable
{
    private readonly string _root;
    private readonly string _clone;
    private readonly string _notesDb;
    private readonly string _githubDb;
    private readonly string _specDb;

    private const string PageName = "security";
    private const string ResolvedCode = "sec";
    private const string ResolvedName = "Security WG";
    private const string StalePrimary = "Stale WG";

    public ReallocateRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reallocwg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _clone = Path.Combine(_root, "clone");
        Directory.CreateDirectory(_clone);
        _notesDb = Path.Combine(_root, "ballot-notes.db");
        _githubDb = Path.Combine(_root, "github.db");
        _specDb = Path.Combine(_root, "fhir-r6.db");
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_root);

    [Fact]
    public async Task DryRun_WritesNothing()
    {
        string head = await BuildCloneAsync();
        SeedGitHubDb();
        SeedSpecDb();
        SeedNote(_notesDb, "hl7-fhir-page-security", "HL7", "fhir", head);

        (int exit, string stdout) = await RunAsync(Options(dryRun: true));

        Assert.Equal(0, exit);
        Assert.Contains("changed: 1", stdout);
        Assert.Contains("dry-run", stdout);

        NoteRecord note = ReadNote(_notesDb, "hl7-fhir-page-security");
        Assert.Equal(StalePrimary, note.WorkGroup);
        Assert.Equal("stale", note.WorkGroupCode);
    }

    [Fact]
    public async Task Write_RestampsPrimaryAndSets()
    {
        string head = await BuildCloneAsync();
        SeedGitHubDb();
        SeedSpecDb();
        SeedNoteWithProse(_notesDb, "hl7-fhir-page-security", "HL7", "fhir", head);
        NoteRecord before = ReadNote(_notesDb, "hl7-fhir-page-security");

        (int exit, _) = await RunAsync(Options(dryRun: false));
        Assert.Equal(0, exit);

        NoteRecord after = ReadNote(_notesDb, "hl7-fhir-page-security");
        Assert.Equal(ResolvedName, after.WorkGroup);
        Assert.Equal(ResolvedCode, after.WorkGroupCode);
        Assert.Equal(ResolvedName, after.WorkGroupNames);
        Assert.Equal(ResolvedCode, after.WorkGroupCodes);

        // Prose + timestamps preserved.
        Assert.Equal(before.ProposedBallotNoteHtml, after.ProposedBallotNoteHtml);
        Assert.Equal("yes", after.NeedsNote);
        Assert.Equal(before.AuthoredAt, after.AuthoredAt);
        Assert.Equal(before.SavedAt, after.SavedAt);
        Assert.Equal(before.GeneratedAt, after.GeneratedAt);
    }

    [Fact]
    public async Task Rerun_IsIdempotent()
    {
        string head = await BuildCloneAsync();
        SeedGitHubDb();
        SeedSpecDb();
        SeedNote(_notesDb, "hl7-fhir-page-security", "HL7", "fhir", head);

        (int first, _) = await RunAsync(Options(dryRun: false));
        Assert.Equal(0, first);

        (int second, string stdout) = await RunAsync(Options(dryRun: false));
        Assert.Equal(0, second);
        Assert.Contains("changed: 0", stdout);
    }

    [Fact]
    public async Task MultiRepo_WithoutRepoFilter_FailsLoudly()
    {
        string head = await BuildCloneAsync();
        SeedGitHubDb();
        SeedSpecDb();
        SeedNote(_notesDb, "hl7-fhir-page-security", "HL7", "fhir", head);
        SeedNote(_notesDb, "hl7-uscore-page-security", "HL7", "US-Core", head);

        (int exit, string stderr) = await RunAsync(Options(dryRun: false, repo: null), captureErr: true);

        Assert.NotEqual(0, exit);
        Assert.Contains("multiple repos", stderr, StringComparison.OrdinalIgnoreCase);

        // Nothing written.
        Assert.Equal(StalePrimary, ReadNote(_notesDb, "hl7-fhir-page-security").WorkGroup);
    }

    [Fact]
    public async Task MissingReferenceDb_FailsPreflight()
    {
        string head = await BuildCloneAsync();
        SeedSpecDb();
        SeedNote(_notesDb, "hl7-fhir-page-security", "HL7", "fhir", head);
        // Note: _githubDb deliberately NOT created.

        (int exit, string stderr) = await RunAsync(Options(dryRun: false), captureErr: true);

        Assert.NotEqual(0, exit);
        Assert.Contains("GitHub source DB not found", stderr);
        Assert.Equal(StalePrimary, ReadNote(_notesDb, "hl7-fhir-page-security").WorkGroup);
    }

    [Fact]
    public async Task StaleClone_FailsGuard_UnlessOverride()
    {
        await BuildCloneAsync();
        SeedGitHubDb();
        SeedSpecDb();
        // Note's HeadSha does not match the clone HEAD.
        SeedNote(_notesDb, "hl7-fhir-page-security", "HL7", "fhir", headSha: "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef");

        (int blocked, string stderr) = await RunAsync(Options(dryRun: false), captureErr: true);
        Assert.NotEqual(0, blocked);
        Assert.Contains("does not match", stderr);
        Assert.Equal(StalePrimary, ReadNote(_notesDb, "hl7-fhir-page-security").WorkGroup);

        (int overridden, _) = await RunAsync(Options(dryRun: false, allowStaleClone: true));
        Assert.Equal(0, overridden);
        Assert.Equal(ResolvedName, ReadNote(_notesDb, "hl7-fhir-page-security").WorkGroup);
    }

    [Fact]
    public async Task ProcessorModeSubmitsOneExpectedRevisionBatchWithoutLocalWrites()
    {
        string head = await BuildCloneAsync();
        SeedGitHubDb();
        SeedSpecDb();
        SeedNote(_notesDb, "hl7-fhir-page-security", "HL7", "fhir", head);
        RecordingProcessorHandler handler = new(HttpStatusCode.OK);

        (int exit, string stdout) = await RunAsync(
            Options(
                dryRun: false,
                processorBaseUrl: new Uri("http://processor")),
            handler: handler);

        Assert.Equal(0, exit);
        Assert.Contains("changed: 1", stdout);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains(
            "api/v1/ballot-notes/maintenance/workgroups/reallocate",
            handler.Path);
        using JsonDocument document = JsonDocument.Parse(handler.Body);
        JsonElement change = document.RootElement
            .GetProperty("changes")[0];
        Assert.False(string.IsNullOrWhiteSpace(
            change.GetProperty("expectedEvidenceRevision").GetString()));
        Assert.Equal(
            StalePrimary,
            ReadNote(_notesDb, "hl7-fhir-page-security").WorkGroup);
    }

    [Fact]
    public async Task ProcessorStaleRevisionFailureLeavesLocalDatabaseUntouched()
    {
        string head = await BuildCloneAsync();
        SeedGitHubDb();
        SeedSpecDb();
        SeedNote(_notesDb, "hl7-fhir-page-security", "HL7", "fhir", head);
        RecordingProcessorHandler handler = new(HttpStatusCode.Conflict);

        (int exit, string stderr) = await RunAsync(
            Options(
                dryRun: false,
                processorBaseUrl: new Uri("http://processor")),
            captureErr: true,
            handler: handler);

        Assert.NotEqual(0, exit);
        Assert.Contains("failed with 409", stderr);
        Assert.Equal(
            StalePrimary,
            ReadNote(_notesDb, "hl7-fhir-page-security").WorkGroup);
    }

    [Fact]
    public async Task DirectWriteRefusesRunBackedDatabase()
    {
        string head = await BuildCloneAsync();
        SeedGitHubDb();
        SeedSpecDb();
        SeedNote(_notesDb, "hl7-fhir-page-security", "HL7", "fhir", head);
        using (SqliteConnection connection =
            new($"Data Source={_notesDb};Pooling=False"))
        {
            connection.Open();
            Exec(
                connection,
                """
                INSERT INTO authoring_processor_modes(
                    ProcessorKind, Mode, Epoch, RevalidationRequired, UpdatedAt)
                VALUES(
                    'ballot-notes', 'run-backed', 1, 0,
                    '2026-09-04T00:00:00.0000000+00:00')
                """);
        }

        (int exit, string stderr) = await RunAsync(
            Options(dryRun: false),
            captureErr: true);

        Assert.NotEqual(0, exit);
        Assert.Contains("use --processor", stderr);
        Assert.Equal(
            StalePrimary,
            ReadNote(_notesDb, "hl7-fhir-page-security").WorkGroup);
    }

    // ── fixture helpers ──────────────────────────────────────────────

    private ReallocateOptions Options(
        bool dryRun,
        string? repo = "HL7/fhir",
        bool allowStaleClone = false,
        Uri? processorBaseUrl = null)
        => new(
            DbPath: _notesDb,
            ClonePath: _clone,
            Repo: repo,
            DryRun: dryRun,
            GitHubDbPath: _githubDb,
            FhirR6DbPath: _specDb,
            FhirSpecDbPath: Path.Combine(_root, "missing-fhir-spec.db"),
            WorkGroupHint: string.Empty,
            AllowStaleClone: allowStaleClone,
            AllowMixedHeads: false,
            ProcessorBaseUrl: processorBaseUrl);

    private static async Task<(int Exit, string Output)> RunAsync(
        ReallocateOptions options,
        bool captureErr = false,
        HttpMessageHandler? handler = null)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalErr = Console.Error;
        StringWriter outWriter = new();
        StringWriter errWriter = new();
        Console.SetOut(outWriter);
        Console.SetError(errWriter);
        try
        {
            int exit = await ReallocateRunner.RunAsync(
                options,
                processorHandler: handler);
            return (exit, captureErr ? errWriter.ToString() : outWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    private async Task<string> BuildCloneAsync()
    {
        string source = Path.Combine(_clone, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(
            Path.Combine(source, $"{PageName}.html"),
            $"<html><body><td id=\"wg\"><a href=\"[%wg {ResolvedCode}%]\">[%wgt {ResolvedCode}%]</a> Work Group</td></body></html>");

        await Git("init", "-q");
        await Git("config", "user.email", "test@example.com");
        await Git("config", "user.name", "Test");
        await Git("config", "commit.gpgsign", "false");
        await Git("add", "-A");
        await Git("commit", "-q", "-m", "seed");
        return (await Git("rev-parse", "HEAD")).Trim();
    }

    private async Task<string> Git(params string[] args)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "git",
            WorkingDirectory = _clone,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using Process process = Process.Start(psi)!;
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        }
        return stdout;
    }

    private void SeedGitHubDb()
    {
        using SqliteConnection conn = new($"Data Source={_githubDb};Pooling=False");
        conn.Open();
        Exec(conn, "CREATE TABLE hl7_workgroups (Id INTEGER PRIMARY KEY, Code TEXT, Name TEXT)");
        Exec(conn, "CREATE TABLE jira_workgroups (Id INTEGER PRIMARY KEY, RepoFullName TEXT, WorkgroupKey TEXT, Name TEXT, WorkGroupCode TEXT)");
        Exec(conn, "CREATE TABLE jira_specs (Id INTEGER PRIMARY KEY, RepoFullName TEXT, SpecKey TEXT, GitUrl TEXT)");
        Exec(conn, "CREATE TABLE jira_spec_artifacts (Id INTEGER PRIMARY KEY, RepoFullName TEXT, SpecKey TEXT, Name TEXT, ArtifactId TEXT, ResourceType TEXT, Workgroup TEXT, Deprecated INTEGER)");
        Exec(conn, "CREATE TABLE jira_spec_pages (Id INTEGER PRIMARY KEY, RepoFullName TEXT, SpecKey TEXT, PageKey TEXT, Name TEXT, Workgroup TEXT, Deprecated INTEGER)");
        Exec(conn, $"INSERT INTO hl7_workgroups (Code, Name) VALUES ('{ResolvedCode}', '{ResolvedName}')");
    }

    private void SeedSpecDb()
    {
        using SqliteConnection conn = new($"Data Source={_specDb};Pooling=False");
        conn.Open();
        Exec(conn, "CREATE TABLE Structures (Id INTEGER PRIMARY KEY, Name TEXT, WorkGroup TEXT)");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void SeedNote(string dbPath, string noteId, string owner, string repo, string headSha)
        => SeedNoteCore(dbPath, noteId, owner, repo, headSha, prose: false);

    private static void SeedNoteWithProse(string dbPath, string noteId, string owner, string repo, string headSha)
        => SeedNoteCore(dbPath, noteId, owner, repo, headSha, prose: true);

    private static void SeedNoteCore(string dbPath, string noteId, string owner, string repo, string headSha, bool prose)
    {
        using BallotNotesDatabase db = new(dbPath, NullLogger<BallotNotesDatabase>.Instance);
        db.Initialize();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        NoteRecord note = new()
        {
            NoteId = noteId,
            Type = "Page",
            Name = PageName,
            RepoOwner = owner,
            RepoName = repo,
            RepoCategory = "FhirCore",
            WorkGroup = StalePrimary,
            WorkGroupCode = "stale",
            WorkGroupNames = StalePrimary,
            WorkGroupCodes = "stale",
            SinceSha = "1111111111111111111111111111111111111111",
            SinceShortSha = "111111111111",
            HeadSha = headSha,
            HeadShortSha = headSha.Length >= 12 ? headSha[..12] : headSha,
            CommitsInWindow = 1,
            TicketsAttributed = 0,
            GeneratedAt = now,
            SavedAt = now,
        };
        List<NoteSourceFileRecord> files =
        [
            new() { NoteId = noteId, Path = $"source/{PageName}.html", Role = "Narrative", TouchedInWindow = true, FileOrder = 0 },
        ];
        db.UpsertUnitEvidence(note, files, [], []);

        if (prose)
        {
            db.UpdateNoteProse(noteId, new BallotNoteProse
            {
                NeedsNote = "yes",
                ProposedBallotNoteHtml = "<blockquote class=\"ballot-note\">drafted</blockquote>",
                RollupSummaryMarkdown = "## Roll-up",
            }, now.AddHours(-2));
        }
    }

    private static NoteRecord ReadNote(string dbPath, string noteId)
    {
        using BallotNotesDatabase db = new(dbPath, NullLogger<BallotNotesDatabase>.Instance, readOnly: true);
        NoteDetail detail = db.GetNote(noteId)!;
        return detail.Note;
    }

    private sealed class RecordingProcessorHandler(
        HttpStatusCode statusCode) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string Path { get; private set; } = string.Empty;
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Body = await request.Content!.ReadAsStringAsync(
                cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = statusCode == HttpStatusCode.OK
                    ? System.Net.Http.Json.JsonContent.Create(
                        new { updatedCount = 1 })
                    : System.Net.Http.Json.JsonContent.Create(
                        new { error = "StaleSourceRevision" }),
            };
        }
    }
}
