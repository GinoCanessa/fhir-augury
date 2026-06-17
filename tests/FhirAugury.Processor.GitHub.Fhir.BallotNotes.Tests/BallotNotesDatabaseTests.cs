using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

public sealed class BallotNotesDatabaseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public BallotNotesDatabaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ballotnotes-db-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "notes.db");
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    private BallotNotesDatabase NewDb()
    {
        BallotNotesDatabase db = new(_dbPath, NullLogger<BallotNotesDatabase>.Instance);
        db.Initialize();
        return db;
    }

    private static NoteRecord Evidence(
        string noteId = "hl7-fhir-artifact-observation",
        string type = "Artifact",
        string name = "Observation",
        string owner = "HL7",
        string repoName = "fhir",
        string workGroupCode = "OO",
        int commits = 2)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new NoteRecord
        {
            NoteId = noteId,
            Type = type,
            Name = name,
            RepoOwner = owner,
            RepoName = repoName,
            RepoCategory = "FhirCore",
            WorkGroup = "Orders and Observations (OO)",
            WorkGroupCode = workGroupCode,
            SinceSha = "1a2b3c",
            SinceShortSha = "1a2b3c",
            HeadSha = "9f8e7d",
            HeadShortSha = "9f8e7d",
            CommitsInWindow = commits,
            TicketsAttributed = 1,
            CurrentBallotNoteHtml = "<blockquote class=\"ballot-note\">current</blockquote>",
            SourceFilesNote = "resolver note",
            GeneratedAt = now,
            SavedAt = now,
        };
    }

    private static (List<NoteSourceFileRecord>, List<NoteCommitRecord>, List<NoteTicketRecord>) Children(string noteId)
        =>
        (
            [new() { NoteId = noteId, Path = "source/observation/observation.xml", Role = "SD", TouchedInWindow = true, FileOrder = 0 }],
            [new() { NoteId = noteId, Sha = "9f8e7dfull", ShortSha = "9f8e7d", AuthorName = "Dev", Subject = "FHIR-1 change", TicketKeys = "FHIR-1", CommitOrder = 0 }],
            [new() { NoteId = noteId, TicketKey = "FHIR-1", Title = "x", CommitCount = 1, TicketOrder = 0 }]
        );

    private static void Seed(BallotNotesDatabase db, NoteRecord evidence)
    {
        (List<NoteSourceFileRecord> f, List<NoteCommitRecord> c, List<NoteTicketRecord> t) = Children(evidence.NoteId);
        db.UpsertUnitEvidence(evidence, f, c, t);
    }

    [Fact]
    public void EnsureSchema_creates_all_tables()
    {
        using BallotNotesDatabase db = NewDb();
        using SqliteConnection conn = new($"Data Source={_dbPath};Pooling=False");
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
    public void UpsertUnitEvidence_is_idempotent_on_slug()
    {
        using BallotNotesDatabase db = NewDb();
        Seed(db, Evidence());
        Seed(db, Evidence()); // re-hydrate same slug

        Assert.Equal(1, db.CountNotes());
        NoteDetail detail = db.GetNote("hl7-fhir-artifact-observation")!;
        Assert.Single(detail.SourceFiles);
        Assert.Single(detail.Commits);
        Assert.Single(detail.Tickets);
        Assert.Equal("awaiting-note", detail.Status);
        Assert.NotNull(detail.Note.HydratedAt);
        Assert.Null(detail.Note.AuthoredAt);
    }

    [Fact]
    public void Rehydration_preserves_authored_prose_and_AuthoredAt()
    {
        using BallotNotesDatabase db = NewDb();
        Seed(db, Evidence(commits: 2));

        BallotNoteProse prose = new()
        {
            NeedsNote = "yes",
            ProposedBallotNoteHtml = "<blockquote class=\"ballot-note\">drafted</blockquote>",
            RollupSummaryMarkdown = "## Roll-up",
            NotesForReviewerMarkdown = "reviewer note",
        };
        Assert.True(db.UpdateNoteProse("hl7-fhir-artifact-observation", prose, DateTimeOffset.UtcNow));

        // Re-hydrate with fresh evidence (different commit count).
        Seed(db, Evidence(commits: 5));

        NoteDetail detail = db.GetNote("hl7-fhir-artifact-observation")!;
        Assert.Equal("authored", detail.Status);
        Assert.NotNull(detail.Note.AuthoredAt);
        Assert.Equal("yes", detail.Note.NeedsNote);
        Assert.Equal("<blockquote class=\"ballot-note\">drafted</blockquote>", detail.Note.ProposedBallotNoteHtml);
        Assert.Equal("## Roll-up", detail.Note.RollupSummaryMarkdown);
        // Evidence still refreshed:
        Assert.Equal(5, detail.Note.CommitsInWindow);
    }

    [Fact]
    public void EvidenceOnly_rehydration_refreshes_resolver_note()
    {
        using BallotNotesDatabase db = NewDb();
        NoteRecord first = Evidence();
        first.SourceFilesNote = "first note";
        Seed(db, first);

        NoteRecord second = Evidence();
        second.SourceFilesNote = "second note";
        Seed(db, second);

        NoteDetail detail = db.GetNote("hl7-fhir-artifact-observation")!;
        Assert.Equal("second note", detail.Note.SourceFilesNote);
    }

    [Fact]
    public void UpdateNoteProse_returns_false_for_unknown_slug()
    {
        using BallotNotesDatabase db = NewDb();
        Assert.False(db.UpdateNoteProse("does-not-exist", new BallotNoteProse(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ListNotes_honours_filters()
    {
        using BallotNotesDatabase db = NewDb();
        Seed(db, Evidence("hl7-fhir-artifact-observation", "Artifact", "Observation", "HL7", "fhir", "OO"));
        Seed(db, Evidence("hl7-fhir-page-security", "Page", "security", "HL7", "fhir", "FHIR-I"));
        Seed(db, Evidence("hl7-uscore-artifact-patient", "Artifact", "patient", "HL7", "US-Core", "OO"));
        db.UpdateNoteProse("hl7-fhir-artifact-observation", new BallotNoteProse { NeedsNote = "yes" }, DateTimeOffset.UtcNow);

        Assert.Equal(2, db.ListNotes(new NoteQueryFilter { Repo = "HL7/fhir" }).Count);
        Assert.Equal(2, db.ListNotes(new NoteQueryFilter { Type = "Artifact" }).Count);
        Assert.Single(db.ListNotes(new NoteQueryFilter { Type = "Page" }));
        Assert.Equal(2, db.ListNotes(new NoteQueryFilter { WorkGroupCode = "OO" }).Count);
        Assert.Single(db.ListNotes(new NoteQueryFilter { Status = "authored" }));
        Assert.Equal(2, db.ListNotes(new NoteQueryFilter { Status = "awaiting-note" }).Count);
        Assert.Single(db.ListNotes(new NoteQueryFilter { NeedsNote = "yes" }));

        NoteListRow authored = Assert.Single(db.ListNotes(new NoteQueryFilter { Status = "authored" }));
        Assert.Equal("hl7-fhir-artifact-observation", authored.NoteId);
        Assert.Equal("authored", authored.Status);
    }

    [Fact]
    public void Run_lifecycle_tracks_status_and_progress()
    {
        using BallotNotesDatabase db = NewDb();
        const string runKey = "HL7/fhir@1a2b3c..9f8e7d";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        db.BeginRun(new NotesRunRecord
        {
            RunKey = runKey,
            RepoOwner = "HL7",
            RepoName = "fhir",
            Status = "running",
            StartedAt = now,
            RunAt = now,
        });

        db.UpdateRunPlan(runKey, unitsTotal: 3, headSha: "9f8e7dfull", headShortSha: "9f8e7d");
        db.BumpRunProgress(runKey, unitsHydrated: 2, commitsInWindow: 7, ticketsAttributed: 4);

        NotesRunRecord running = db.GetRun(runKey)!;
        Assert.Equal("running", running.Status);
        Assert.Equal(3, running.UnitsTotal);
        Assert.Equal(2, running.UnitsHydrated);
        Assert.Equal(7, running.CommitsInWindow);
        Assert.Equal(4, running.TicketsAttributed);
        Assert.Equal("9f8e7dfull", running.HeadSha);

        db.FinishRun(runKey, "completed", null);
        NotesRunRecord done = db.GetLatestRun()!;
        Assert.Equal("completed", done.Status);
        Assert.NotNull(done.CompletedAt);
        Assert.Equal(string.Empty, done.Error);
    }
}
