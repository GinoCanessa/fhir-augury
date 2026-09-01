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
    public void ListNoteIds_returns_all_in_rowid_order_and_honours_repo_filter()
    {
        using BallotNotesDatabase db = NewDb();
        Seed(db, Evidence("hl7-fhir-artifact-observation", "Artifact", "Observation", "HL7", "fhir", "OO"));
        Seed(db, Evidence("hl7-fhir-page-security", "Page", "security", "HL7", "fhir", "FHIR-I"));
        Seed(db, Evidence("hl7-uscore-artifact-patient", "Artifact", "patient", "HL7", "US-Core", "OO"));

        Assert.Equal(
            ["hl7-fhir-artifact-observation", "hl7-fhir-page-security", "hl7-uscore-artifact-patient"],
            db.ListNoteIds(null));
        Assert.Equal(
            ["hl7-fhir-artifact-observation", "hl7-fhir-page-security"],
            db.ListNoteIds("HL7/fhir"));
        Assert.Single(db.ListNoteIds("HL7/US-Core"));
        Assert.Empty(db.ListNoteIds("HL7/missing"));
    }

    [Fact]
    public void UpdateNoteWorkGroups_UpdatesOnlyWgColumns_PreservesProseAndTimestamps()
    {
        using BallotNotesDatabase db = NewDb();
        Seed(db, Evidence());

        DateTimeOffset authoredAt = DateTimeOffset.UtcNow.AddHours(-3);
        BallotNoteProse prose = new()
        {
            NeedsNote = "yes",
            ProposedBallotNoteHtml = "<blockquote class=\"ballot-note\">drafted</blockquote>",
            RollupSummaryMarkdown = "## Roll-up",
            NotesForReviewerMarkdown = "reviewer note",
        };
        Assert.True(db.UpdateNoteProse("hl7-fhir-artifact-observation", prose, authoredAt));

        NoteDetail before = db.GetNote("hl7-fhir-artifact-observation")!;

        Assert.True(db.UpdateNoteWorkGroups(
            "hl7-fhir-artifact-observation",
            workGroup: "FHIR Infrastructure (FHIR-I)",
            workGroupCode: "fhir",
            workGroupNames: "FHIR Infrastructure (FHIR-I);Orders and Observations (OO)",
            workGroupCodes: "fhir;oo"));

        NoteDetail after = db.GetNote("hl7-fhir-artifact-observation")!;

        // WG columns re-stamped.
        Assert.Equal("FHIR Infrastructure (FHIR-I)", after.Note.WorkGroup);
        Assert.Equal("fhir", after.Note.WorkGroupCode);
        Assert.Equal("FHIR Infrastructure (FHIR-I);Orders and Observations (OO)", after.Note.WorkGroupNames);
        Assert.Equal("fhir;oo", after.Note.WorkGroupCodes);

        // Everything else preserved.
        Assert.Equal(before.Note.ProposedBallotNoteHtml, after.Note.ProposedBallotNoteHtml);
        Assert.Equal("yes", after.Note.NeedsNote);
        Assert.Equal(before.Note.RollupSummaryMarkdown, after.Note.RollupSummaryMarkdown);
        Assert.Equal(before.Note.AuthoredAt, after.Note.AuthoredAt);
        Assert.Equal(before.Note.SavedAt, after.Note.SavedAt);
        Assert.Equal(before.Note.GeneratedAt, after.Note.GeneratedAt);
        Assert.Equal(before.Note.HydratedAt, after.Note.HydratedAt);
        Assert.Equal("authored", after.Status);
    }

    [Fact]
    public void UpdateNoteWorkGroups_returns_false_for_unknown_slug()
    {
        using BallotNotesDatabase db = NewDb();
        Assert.False(db.UpdateNoteWorkGroups("does-not-exist", "wg", "code", "wg", "code"));
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

    [Fact]
    public void WindowLabel_round_trips_through_note_and_run()
    {
        using BallotNotesDatabase db = NewDb();

        NoteRecord evidence = Evidence();
        evidence.WindowLabel = "R6 Ballot 4";
        Seed(db, evidence);

        NoteDetail detail = db.GetNote("hl7-fhir-artifact-observation")!;
        Assert.Equal("R6 Ballot 4", detail.Note.WindowLabel);

        const string runKey = "HL7/fhir@1a2b3c..9f8e7d";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.BeginRun(new NotesRunRecord
        {
            RunKey = runKey,
            RepoOwner = "HL7",
            RepoName = "fhir",
            WindowLabel = "R6 Ballot 4",
            Status = "running",
            StartedAt = now,
            RunAt = now,
        });

        Assert.Equal("R6 Ballot 4", db.GetRun(runKey)!.WindowLabel);
        Assert.Equal("R6 Ballot 4", db.GetLatestRun()!.WindowLabel);
    }

    [Fact]
    public void EnsureSchema_backfills_WindowLabel_on_legacy_db()
    {
        // Build the current schema, then drop the WindowLabel columns to mimic a
        // pre-Phase-1 DB that has every other column but not WindowLabel.
        using (BallotNotesDatabase seed = NewDb()) { }
        using (SqliteConnection legacy = new($"Data Source={_dbPath};Pooling=False"))
        {
            legacy.Open();
            using SqliteCommand cmd = legacy.CreateCommand();
            cmd.CommandText =
                "ALTER TABLE notes DROP COLUMN WindowLabel;" +
                "ALTER TABLE notes_runs DROP COLUMN WindowLabel;";
            cmd.ExecuteNonQuery();
        }
        Assert.False(HasColumn("notes", "WindowLabel"));
        Assert.False(HasColumn("notes_runs", "WindowLabel"));

        using BallotNotesDatabase db = NewDb(); // Initialize() → EnsureSchema()

        Assert.True(HasColumn("notes", "WindowLabel"));
        Assert.True(HasColumn("notes_runs", "WindowLabel"));
    }

    [Fact]
    public void Lineage_workgroup_sets_round_trip_through_note()
    {
        using BallotNotesDatabase db = NewDb();
        NoteRecord evidence = Evidence();
        evidence.ListedWorkGroupNames = "FHIR Infrastructure (FHIR-I)";
        evidence.ListedWorkGroupCodes = "fhir";
        evidence.IndexWorkGroupNames = "Patient Administration (PA)";
        evidence.IndexWorkGroupCodes = "pa";
        evidence.AppliedWorkGroupNames = "FHIR Infrastructure (FHIR-I);Orders and Observations (OO)";
        evidence.AppliedWorkGroupCodes = "fhir;oo";
        Seed(db, evidence);

        NoteDetail detail = db.GetNote("hl7-fhir-artifact-observation")!;
        Assert.Equal("FHIR Infrastructure (FHIR-I)", detail.Note.ListedWorkGroupNames);
        Assert.Equal("fhir", detail.Note.ListedWorkGroupCodes);
        Assert.Equal("Patient Administration (PA)", detail.Note.IndexWorkGroupNames);
        Assert.Equal("pa", detail.Note.IndexWorkGroupCodes);
        Assert.Equal("FHIR Infrastructure (FHIR-I);Orders and Observations (OO)", detail.Note.AppliedWorkGroupNames);
        Assert.Equal("fhir;oo", detail.Note.AppliedWorkGroupCodes);
    }

    [Fact]
    public void EnsureSchema_backfills_lineage_columns_on_legacy_db()
    {
        // Build the current schema, then drop the six lineage columns to mimic a
        // pre-feature DB that has every other column but not the lineage sets.
        using (BallotNotesDatabase seed = NewDb()) { }
        using (SqliteConnection legacy = new($"Data Source={_dbPath};Pooling=False"))
        {
            legacy.Open();
            using SqliteCommand cmd = legacy.CreateCommand();
            cmd.CommandText =
                "ALTER TABLE notes DROP COLUMN ListedWorkGroupNames;" +
                "ALTER TABLE notes DROP COLUMN ListedWorkGroupCodes;" +
                "ALTER TABLE notes DROP COLUMN IndexWorkGroupNames;" +
                "ALTER TABLE notes DROP COLUMN IndexWorkGroupCodes;" +
                "ALTER TABLE notes DROP COLUMN AppliedWorkGroupNames;" +
                "ALTER TABLE notes DROP COLUMN AppliedWorkGroupCodes;";
            cmd.ExecuteNonQuery();
        }
        foreach (string col in new[] { "ListedWorkGroupNames", "ListedWorkGroupCodes", "IndexWorkGroupNames", "IndexWorkGroupCodes", "AppliedWorkGroupNames", "AppliedWorkGroupCodes" })
        {
            Assert.False(HasColumn("notes", col));
        }

        using BallotNotesDatabase db = NewDb(); // Initialize() → EnsureSchema()

        foreach (string col in new[] { "ListedWorkGroupNames", "ListedWorkGroupCodes", "IndexWorkGroupNames", "IndexWorkGroupCodes", "AppliedWorkGroupNames", "AppliedWorkGroupCodes" })
        {
            Assert.True(HasColumn("notes", col));
        }

        // And a fresh insert+read works on the migrated DB.
        NoteRecord evidence = Evidence();
        evidence.AppliedWorkGroupNames = "Orders and Observations (OO)";
        evidence.AppliedWorkGroupCodes = "oo";
        Seed(db, evidence);
        Assert.Equal("oo", db.GetNote("hl7-fhir-artifact-observation")!.Note.AppliedWorkGroupCodes);
    }

    [Fact]
    public void ChangeImpact_and_category_round_trip_through_ticket_read()
    {
        using BallotNotesDatabase db = NewDb();
        string noteId = "hl7-fhir-artifact-observation";
        (List<NoteSourceFileRecord> f, List<NoteCommitRecord> c, _) = Children(noteId);

        // Regression fixture for #8: a Non-substantive ticket (FHIR-56060)
        // must carry its classification verbatim through the read path.
        List<NoteTicketRecord> tickets =
        [
            new() { NoteId = noteId, TicketKey = "FHIR-56060", Title = "clarify", ChangeImpact = "Non-substantive", ChangeCategory = "Clarification", IssueType = "Change Request", RelatedTicketKeys = "FHIR-200;FHIR-300", CommitCount = 1, TicketOrder = 0 },
            new() { NoteId = noteId, TicketKey = "FHIR-1", Title = "break", ChangeImpact = "Non-compatible", ChangeCategory = "", IssueType = "Technical Correction", CommitCount = 1, TicketOrder = 1 },
        ];
        db.UpsertUnitEvidence(Evidence(noteId), f, c, tickets);

        NoteDetail detail = db.GetNote(noteId)!;
        NoteTicketRecord nonSub = detail.Tickets.Single(t => t.TicketKey == "FHIR-56060");
        Assert.Equal("Non-substantive", nonSub.ChangeImpact);
        Assert.Equal("Clarification", nonSub.ChangeCategory);
        Assert.Equal("Change Request", nonSub.IssueType);
        Assert.Equal("FHIR-200;FHIR-300", nonSub.RelatedTicketKeys);
        Assert.Equal("Non-compatible", detail.Tickets.Single(t => t.TicketKey == "FHIR-1").ChangeImpact);
        Assert.Equal("Technical Correction", detail.Tickets.Single(t => t.TicketKey == "FHIR-1").IssueType);
    }

    [Fact]
    public void Current_note_classification_round_trips()
    {
        using BallotNotesDatabase db = NewDb();
        NoteRecord evidence = Evidence();
        evidence.CurrentNoteIsAuguryGenerated = true;
        evidence.PreservedHandAuthoredHtml = "<blockquote class=\"stu-note\">hand</blockquote>";
        Seed(db, evidence);

        NoteDetail detail = db.GetNote("hl7-fhir-artifact-observation")!;
        Assert.True(detail.Note.CurrentNoteIsAuguryGenerated);
        Assert.Equal("<blockquote class=\"stu-note\">hand</blockquote>", detail.Note.PreservedHandAuthoredHtml);
    }

    [Fact]
    public void Structural_changes_round_trip_through_upsert_and_read()
    {
        using BallotNotesDatabase db = NewDb();
        string noteId = "hl7-fhir-artifact-observation";
        (List<NoteSourceFileRecord> f, List<NoteCommitRecord> c, List<NoteTicketRecord> t) = Children(noteId);

        List<NoteStructuralChangeRecord> structural =
        [
            new() { NoteId = noteId, SourcePath = "source/observation/structuredefinition-observation.xml", ElementPath = "Observation.status", ChangeKind = "Cardinality", Detail = "cardinality 1..1→0..1", TicketKeys = "FHIR-1;FHIR-2", ChangeOrder = 0 },
            new() { NoteId = noteId, SourcePath = "source/observation/structuredefinition-observation.xml", ElementPath = "Observation.value[x]", ChangeKind = "MustSupport", Detail = "mustSupport false→true", TicketKeys = "", ChangeOrder = 1 },
        ];
        db.UpsertUnitEvidence(Evidence(noteId), f, c, t, structural);

        NoteDetail detail = db.GetNote(noteId)!;
        Assert.Equal(2, detail.StructuralChanges.Count);
        NoteStructuralChangeRecord card = detail.StructuralChanges.Single(s => s.ChangeKind == "Cardinality");
        Assert.Equal("Observation.status", card.ElementPath);
        Assert.Equal("cardinality 1..1→0..1", card.Detail);
        Assert.Equal("FHIR-1;FHIR-2", card.TicketKeys);
        Assert.Equal("MustSupport", detail.StructuralChanges.Single(s => s.ElementPath == "Observation.value[x]").ChangeKind);
    }

    [Fact]
    public void Extension_refs_round_trip_through_upsert_and_read()
    {
        using BallotNotesDatabase db = NewDb();
        string noteId = "hl7-fhir-artifact-observation";
        (List<NoteSourceFileRecord> f, List<NoteCommitRecord> c, List<NoteTicketRecord> t) = Children(noteId);

        List<NoteExtensionRefRecord> refs =
        [
            new() { NoteId = noteId, ExtensionUrl = "http://example.org/ext/replaced", ExtensionName = "PatientGenderExt", ReplacementCoreElement = "Patient.gender", Rationale = "replaced by Patient.gender", RefOrder = 0 },
        ];
        db.UpsertUnitEvidence(Evidence(noteId), f, c, t, [], refs);

        NoteDetail detail = db.GetNote(noteId)!;
        NoteExtensionRefRecord one = Assert.Single(detail.ExtensionRefs);
        Assert.Equal("PatientGenderExt", one.ExtensionName);
        Assert.Equal("Patient.gender", one.ReplacementCoreElement);
    }

    private bool HasColumn(string table, string column)
    {
        using SqliteConnection conn = new($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
