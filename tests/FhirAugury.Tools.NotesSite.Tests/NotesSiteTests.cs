using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;
using FhirAugury.Tools.NotesSite.Report;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.NotesSite.Tests;

/// <summary>
/// Exercises the notes-site renderer: seeding the BallotNotes processor schema
/// via <see cref="BallotNotesDatabase"/>, the <see cref="NotesSpaEmitter"/>
/// self-contained-SPA emit, and the <c>report</c> overwrite guard. Persistence /
/// upsert behavior is pinned by the processor's own tests. Raw connections use
/// <c>;Pooling=False</c>.
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

    /// <summary>Seeds one note + its children and a run row via the processor schema.</summary>
    private static void Seed(BallotNotesDatabase db)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        const string noteId = "hl7-fhir-artifact-observation";
        NoteRecord note = new()
        {
            NoteId = noteId,
            Type = "Artifact",
            Name = "Observation",
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
            TicketsAttributed = 2,
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
            new() { NoteId = noteId, TicketKey = "FHIR-2", Title = "A correction", Resolution = "Persuasive", WorkGroup = "OO", ChangeImpact = "Non-substantive", IssueType = "Technical Correction", CommitCount = 1, TicketOrder = 1 },
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

        db.UpsertUnitEvidence(note, files, commits, tickets);
        db.BeginRun(run);
    }

    [Fact]
    public void Emit_Writes_Single_Spa_With_Inlined_Db_And_Assets()
    {
        string dbPath = Path.Combine(_tempDir, "emit.db");
        using (BallotNotesDatabase db = new(dbPath, NullLogger<BallotNotesDatabase>.Instance))
        {
            db.Initialize();
            Seed(db);
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
        using (BallotNotesDatabase db = new(dbPath, NullLogger<BallotNotesDatabase>.Instance))
        {
            db.Initialize();
            Seed(db);
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
        using (BallotNotesDatabase db = new(dbPath, NullLogger<BallotNotesDatabase>.Instance))
        {
            db.Initialize();
            Seed(db);
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

    [Fact]
    public void Emit_App_Js_Ships_CopyForAi_Affordance()
    {
        string dbPath = Path.Combine(_tempDir, "copy-ai.db");
        using (BallotNotesDatabase db = new(dbPath, NullLogger<BallotNotesDatabase>.Instance))
        {
            db.Initialize();
            Seed(db);
        }

        string outDir = Path.Combine(_tempDir, "copy-ai-site");
        new NotesSpaEmitter(dbPath, "x").Emit(outDir);

        string appJs = File.ReadAllText(Path.Combine(outDir, "assets", "app.js"));
        Assert.Contains("Copy for AI", appJs);
        Assert.Contains("copyForAi", appJs);
        Assert.Contains("installCopyButton", appJs);
        Assert.Contains("setCopyExport", appJs);
        Assert.Contains("clearCopyExport", appJs);
        Assert.Contains("execCommand", appJs);
        Assert.Contains("htmlToMarkdown", appJs);

        Assert.Contains("document.title", appJs);
        Assert.Contains("setDocTitle", appJs);

        string appCss = File.ReadAllText(Path.Combine(outDir, "assets", "app.css"));
        Assert.Contains(".copy-ai", appCss);
    }

    [Fact]
    public void Emit_App_Js_Ships_Grouping_Window_And_Consolidation_Rendering()
    {
        string dbPath = Path.Combine(_tempDir, "grouping.db");
        using (BallotNotesDatabase db = new(dbPath, NullLogger<BallotNotesDatabase>.Instance))
        {
            db.Initialize();
            Seed(db);
        }

        string outDir = Path.Combine(_tempDir, "grouping-site");
        new NotesSpaEmitter(dbPath, "x").Emit(outDir);

        string appJs = File.ReadAllText(Path.Combine(outDir, "assets", "app.js"));
        // Phase 2: change-impact grouping helper + the four bucket labels.
        Assert.Contains("changeImpactBucket", appJs);
        Assert.Contains("Compatible substantive", appJs);
        Assert.Contains("Unclassified", appJs);
        Assert.Contains("ChangeImpact, ChangeCategory", appJs);
        // Technical Correction issue-Type group (lowest-ranked, after Unclassified).
        Assert.Contains("ticketGroup", appJs);
        Assert.Contains("Technical Correction", appJs);
        Assert.Contains("IssueType", appJs);
        // Phase 1: human-readable window label.
        Assert.Contains("Changes since ", appJs);
        Assert.Contains("WindowLabel", appJs);
        // Phase 3: single-note consolidation surfacing.
        Assert.Contains("PreservedHandAuthoredHtml", appJs);
        Assert.Contains("consolidation-status", appJs);
        // Phase 4: authored note HTML rendered through the sanitizer.
        Assert.Contains("ProposedBallotNoteHtml", appJs);
        Assert.Contains("htmlBlock", appJs);

        string appCss = File.ReadAllText(Path.Combine(outDir, "assets", "app.css"));
        Assert.Contains(".impact-header", appCss);
        Assert.Contains(".tag", appCss);
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
