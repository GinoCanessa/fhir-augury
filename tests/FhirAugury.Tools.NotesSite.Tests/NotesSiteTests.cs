using System.Text.Json;
using FhirAugury.Tools.NotesSite.Contracts;
using FhirAugury.Tools.NotesSite.Database;
using FhirAugury.Tools.NotesSite.Database.Records;
using FhirAugury.Tools.NotesSite.Report;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.NotesSite.Tests;

/// <summary>
/// Exercises the notes DB schema, the <see cref="NotesDatabase.SaveNote"/>
/// upsert, the <c>write</c>-verb mapping, and the <see cref="NotesSpaEmitter"/>
/// self-contained-SPA emit. Raw connections use <c>;Pooling=False</c>.
/// </summary>
public sealed class NotesSiteTests : IDisposable
{
    private readonly string _tempDir;

    public NotesSiteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "notes-site-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    private (NoteRecord, List<NoteSourceFileRecord>, List<NoteCommitRecord>, List<NoteTicketRecord>, NotesRunRecord)
        SampleNote(string noteId = "hl7-fhir-artifact-observation", string name = "Observation")
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NoteRecord note = new()
        {
            NoteId = noteId,
            Type = "Artifact",
            Name = name,
            RepoOwner = "HL7",
            RepoName = "fhir",
            RepoCategory = "FhirCore",
            WorkGroup = "Orders and Observations (OO)",
            WorkGroupCode = "OO",
            SinceSha = "1a2b3c",
            SinceShortSha = "1a2b3c",
            HeadSha = "9f8e7d",
            HeadShortSha = "9f8e7d",
            CommitsInWindow = 2,
            TicketsAttributed = 1,
            NeedsNote = "yes",
            ProposedBallotNoteHtml = "<blockquote class=\"ballot-note\">draft</blockquote>",
            RollupSummaryMarkdown = "## Summary\n- a change",
            GeneratedAt = now,
            SavedAt = now,
        };
        List<NoteSourceFileRecord> files =
        [
            new() { NoteId = noteId, Path = "source/observation/structuredefinition-observation.xml", Role = "SD", TouchedInWindow = true, FileOrder = 0 },
        ];
        List<NoteCommitRecord> commits =
        [
            new() { NoteId = noteId, Sha = "1a2b3cfull", ShortSha = "1a2b3c", AuthorName = "Jane", Subject = "FHIR-1 change", TicketKeys = "FHIR-1", CommitOrder = 0 },
        ];
        List<NoteTicketRecord> tickets =
        [
            new() { NoteId = noteId, TicketKey = "FHIR-1", Title = "A change", Resolution = "Persuasive", WorkGroup = "OO", CommitCount = 1, TicketOrder = 0 },
        ];
        NotesRunRecord run = new()
        {
            RunKey = "HL7/fhir@1a2b3c..9f8e7d",
            RepoOwner = "HL7",
            RepoName = "fhir",
            RepoCategory = "FhirCore",
            SinceSha = "1a2b3c",
            SinceShortSha = "1a2b3c",
            HeadSha = "9f8e7d",
            HeadShortSha = "9f8e7d",
            RunAt = now,
        };
        return (note, files, commits, tickets, run);
    }

    [Fact]
    public void Initialize_Creates_All_Tables()
    {
        string dbPath = Path.Combine(_tempDir, "schema.db");
        using NotesDatabase db = new(dbPath, NullLogger<NotesDatabase>.Instance);
        db.Initialize();

        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        conn.Open();
        foreach (string table in new[] { "notes", "note_source_files", "note_commits", "note_tickets", "notes_runs" })
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n";
            cmd.Parameters.AddWithValue("$n", table);
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public void SaveNote_Then_ReWrite_Replaces_Rows_Idempotently()
    {
        string dbPath = Path.Combine(_tempDir, "upsert.db");
        using NotesDatabase db = new(dbPath, NullLogger<NotesDatabase>.Instance);
        db.Initialize();

        (NoteRecord note, var files, var commits, var tickets, NotesRunRecord run) = SampleNote();
        db.SaveNote(note, files, commits, tickets, run);
        db.SaveNote(note, files, commits, tickets, run); // re-write same unit

        Assert.Equal(1, db.CountNotes());
        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        conn.Open();
        Assert.Equal(1L, Count(conn, "SELECT COUNT(*) FROM note_source_files"));
        Assert.Equal(1L, Count(conn, "SELECT COUNT(*) FROM note_commits"));
        Assert.Equal(1L, Count(conn, "SELECT COUNT(*) FROM note_tickets"));
        Assert.Equal(1L, Count(conn, "SELECT COUNT(*) FROM notes_runs"));
    }

    [Fact]
    public void Two_Distinct_Notes_Coexist()
    {
        string dbPath = Path.Combine(_tempDir, "two.db");
        using NotesDatabase db = new(dbPath, NullLogger<NotesDatabase>.Instance);
        db.Initialize();

        (NoteRecord a, var fa, var ca, var ta, NotesRunRecord ra) = SampleNote();
        (NoteRecord b, var fb, var cb, var tb, NotesRunRecord rb) = SampleNote("hl7-fhir-page-security", "security");
        b.Type = "Page";
        db.SaveNote(a, fa, ca, ta, ra);
        db.SaveNote(b, fb, cb, tb, rb);

        Assert.Equal(2, db.CountNotes());
    }

    [Fact]
    public async Task WriteRunner_Maps_Payload_From_File()
    {
        string dbPath = Path.Combine(_tempDir, "write.db");
        NoteWritePayload payload = new()
        {
            Type = "page",
            Name = "Security",
            RepoOwner = "HL7",
            RepoName = "fhir",
            WorkGroup = "FHIR Infrastructure (FHIR-I)",
            NeedsNote = "NO",
            HeadSha = "abcdef1234567890",
            SourceFiles = [new NoteSourceFilePayload { Path = "source/security.html", Role = "page", TouchedInWindow = true }],
            Commits = [new NoteCommitPayload { Sha = "deadbeefcafef00d", AuthorName = "Ed", TicketKeys = ["FHIR-9"] }],
            Tickets = [new NoteTicketPayload { Key = "FHIR-9", Title = "x" }],
        };
        string inPath = Path.Combine(_tempDir, "p.json");
        await File.WriteAllTextAsync(inPath, JsonSerializer.Serialize(payload));

        int exit = await WriteRunner.RunAsync(new WriteOptions(dbPath, inPath, DropTables: true));
        Assert.Equal(0, exit);

        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT NoteId, Type, NeedsNote, HeadShortSha FROM notes";
        using SqliteDataReader r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("hl7-fhir-page-security", r.GetString(0)); // normalized + slugified
        Assert.Equal("Page", r.GetString(1));                    // type normalized
        Assert.Equal("no", r.GetString(2));                      // needsNote normalized
        Assert.Equal("abcdef123456", r.GetString(3));            // short sha derived
    }

    [Fact]
    public async Task WriteRunner_Rejects_Invalid_Type()
    {
        string dbPath = Path.Combine(_tempDir, "bad.db");
        string inPath = Path.Combine(_tempDir, "bad.json");
        await File.WriteAllTextAsync(inPath,
            JsonSerializer.Serialize(new NoteWritePayload { Type = "Widget", Name = "x", RepoOwner = "HL7", RepoName = "fhir" }));

        int exit = await WriteRunner.RunAsync(new WriteOptions(dbPath, inPath, DropTables: false));
        Assert.Equal(2, exit);
        Assert.False(File.Exists(dbPath));
    }

    [Fact]
    public void Emit_Writes_Single_Spa_With_Inlined_Db_And_Assets()
    {
        string dbPath = Path.Combine(_tempDir, "emit.db");
        using (NotesDatabase db = new(dbPath, NullLogger<NotesDatabase>.Instance))
        {
            db.Initialize();
            (NoteRecord note, var files, var commits, var tickets, NotesRunRecord run) = SampleNote();
            db.SaveNote(note, files, commits, tickets, run);
        }

        string outDir = Path.Combine(_tempDir, "site");
        new NotesSpaEmitter(dbPath, "Test Notes").Emit(outDir);

        string indexPath = Path.Combine(outDir, "index.html");
        Assert.True(File.Exists(indexPath));
        string index = File.ReadAllText(indexPath);

        Assert.False(string.IsNullOrEmpty(ExtractDbBlob(index)));
        Assert.Contains("window.__RUN__", index);
        Assert.Contains("Test Notes", index);

        foreach (string asset in new[] { "sql-wasm.js", "sql-wasm.wasm", "app.js", "app.css", "purify.min.js", "marked.min.js" })
        {
            Assert.True(File.Exists(Path.Combine(outDir, "assets", asset)), $"missing asset {asset}");
        }

        string[] rootHtml = Directory.GetFiles(outDir, "*.html", SearchOption.TopDirectoryOnly);
        Assert.Single(rootHtml);
        Assert.Equal("index.html", Path.GetFileName(rootHtml[0]));
    }

    [Fact]
    public void Emit_Snapshot_Contains_Rows()
    {
        string dbPath = Path.Combine(_tempDir, "snap.db");
        using (NotesDatabase db = new(dbPath, NullLogger<NotesDatabase>.Instance))
        {
            db.Initialize();
            (NoteRecord note, var files, var commits, var tickets, NotesRunRecord run) = SampleNote();
            db.SaveNote(note, files, commits, tickets, run);
        }

        string outDir = Path.Combine(_tempDir, "snap-site");
        new NotesSpaEmitter(dbPath, "x").Emit(outDir);
        byte[] dbBytes = Convert.FromBase64String(ExtractDbBlob(File.ReadAllText(Path.Combine(outDir, "index.html"))));

        string snapPath = Path.Combine(_tempDir, "decoded.db");
        File.WriteAllBytes(snapPath, dbBytes);
        using SqliteConnection conn = new($"Data Source={snapPath};Pooling=False");
        conn.Open();
        Assert.Equal(1L, Count(conn, "SELECT COUNT(*) FROM notes WHERE Name='Observation'"));
        Assert.Equal(1L, Count(conn, "SELECT COUNT(*) FROM note_commits WHERE TicketKeys='FHIR-1'"));
    }

    [Fact]
    public async Task ReportRunner_Overwrite_Guard()
    {
        string dbPath = Path.Combine(_tempDir, "guard.db");
        using (NotesDatabase db = new(dbPath, NullLogger<NotesDatabase>.Instance))
        {
            db.Initialize();
            (NoteRecord note, var files, var commits, var tickets, NotesRunRecord run) = SampleNote();
            db.SaveNote(note, files, commits, tickets, run);
        }
        string outDir = Path.Combine(_tempDir, "guarded");

        int first = await ReportRunner.RunAsync(new ReportOptions(dbPath, outDir, "x", Force: false));
        Assert.Equal(0, first);

        int second = await ReportRunner.RunAsync(new ReportOptions(dbPath, outDir, "x", Force: false));
        Assert.Equal(1, second); // guarded without --force

        int forced = await ReportRunner.RunAsync(new ReportOptions(dbPath, outDir, "x", Force: true));
        Assert.Equal(0, forced);
    }

    [Fact]
    public async Task ReportRunner_Missing_Db_Fails()
    {
        int exit = await ReportRunner.RunAsync(
            new ReportOptions(Path.Combine(_tempDir, "nope.db"), Path.Combine(_tempDir, "out"), "x", Force: false));
        Assert.Equal(1, exit);
    }

    private static long Count(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static string ExtractDbBlob(string html)
    {
        const string marker = "window.__DB__='";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += marker.Length;
        int end = html.IndexOf('\'', start);
        return end < 0 ? string.Empty : html.Substring(start, end - start);
    }
}
