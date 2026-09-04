using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Processing.Contracts;
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
[Collection("ConsoleRedirect")]
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
        Assert.Contains("window.__DBGZ__=1", index, StringComparison.Ordinal);
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
        byte[] dbBytes = Decompress(Convert.FromBase64String(ExtractDbBlob(File.ReadAllText(Path.Combine(outDir, "index.html")))));
        Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(dbBytes, 0, 16));

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
    public void Cli_SnapshotModeRequiresBothFilesAndRejectsLegacyDbMix()
    {
        Assert.False(CliOptions.TryParseReport(
            ["--snapshot-db", "snapshot.db"],
            out _,
            out string? missingDescriptor));
        Assert.Contains("--snapshot-descriptor", missingDescriptor, StringComparison.Ordinal);

        Assert.False(CliOptions.TryParseReport(
            [
                "--db", "live.db",
                "--snapshot-db", "snapshot.db",
                "--snapshot-descriptor", "snapshot.json",
            ],
            out _,
            out string? mixed));
        Assert.Contains("mutually exclusive", mixed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotModeEmbedsExactImmutableBytesAndWritesProvenanceManifest()
    {
        NotesSnapshotFixture snapshot = await CreateNotesSnapshotAsync(
            sequence: 4,
            snapshotId: "notes-snapshot-4");
        string outDir = Path.Combine(_tempDir, "snapshot-site");

        int exit = await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            outDir,
            "Snapshot Notes",
            Force: false,
            SnapshotDbPath: snapshot.DatabasePath,
            SnapshotDescriptorPath: snapshot.DescriptorPath));
        Assert.Equal(0, exit);

        string html = await File.ReadAllTextAsync(Path.Combine(outDir, "index.html"));
        byte[] embedded = Decompress(Convert.FromBase64String(ExtractDbBlob(html)));
        Assert.Equal(await File.ReadAllBytesAsync(snapshot.DatabasePath), embedded);
        Assert.Contains(snapshot.Descriptor.RunId, html, StringComparison.Ordinal);
        Assert.Contains(snapshot.Descriptor.SnapshotId, html, StringComparison.Ordinal);

        SiteBuildManifest manifest = SiteBuildManifest.Read(Path.Combine(
            outDir,
            SiteBuildManifest.FileName));
        Assert.Equal(snapshot.Descriptor.SnapshotId, manifest.SnapshotId);
        Assert.Equal(4, manifest.SnapshotSequence);
        Assert.Equal(1, manifest.IncludedNoteCount);

        DateTime firstWrite = File.GetLastWriteTimeUtc(Path.Combine(
            outDir,
            SiteBuildManifest.FileName));
        await Task.Delay(50);
        int second = await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            outDir,
            "Snapshot Notes",
            Force: false,
            SnapshotDbPath: snapshot.DatabasePath,
            SnapshotDescriptorPath: snapshot.DescriptorPath));
        Assert.Equal(0, second);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(Path.Combine(
            outDir,
            SiteBuildManifest.FileName)));
    }

    [Fact]
    public async Task SameSnapshotWithDifferentTitleRebuildsWithNewBuildIdentity()
    {
        NotesSnapshotFixture snapshot = await CreateNotesSnapshotAsync(
            sequence: 5,
            snapshotId: "notes-title");
        string outDir = Path.Combine(_tempDir, "notes-title-site");

        Assert.Equal(0, await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            outDir,
            "First title",
            Force: false,
            SnapshotDbPath: snapshot.DatabasePath,
            SnapshotDescriptorPath: snapshot.DescriptorPath)));
        SiteBuildManifest first = SiteBuildManifest.Read(Path.Combine(
            outDir,
            SiteBuildManifest.FileName));

        Assert.Equal(0, await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            outDir,
            "Second title",
            Force: false,
            SnapshotDbPath: snapshot.DatabasePath,
            SnapshotDescriptorPath: snapshot.DescriptorPath)));
        SiteBuildManifest second = SiteBuildManifest.Read(Path.Combine(
            outDir,
            SiteBuildManifest.FileName));

        Assert.NotEqual(first.BuildIdentity, second.BuildIdentity);
        Assert.Equal("Second title", second.Title);
        Assert.Contains(
            "Second title",
            await File.ReadAllTextAsync(Path.Combine(outDir, "index.html")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotDescriptorChecksumAndCountFailuresAreRejected()
    {
        NotesSnapshotFixture checksum = await CreateNotesSnapshotAsync(
            sequence: 5,
            snapshotId: "notes-checksum");
        await WriteDescriptorAsync(
            checksum.DescriptorPath,
            checksum.Descriptor with { Sha256 = new string('0', 64) });
        int checksumExit = await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            Path.Combine(_tempDir, "bad-checksum"),
            "x",
            Force: false,
            SnapshotDbPath: checksum.DatabasePath,
            SnapshotDescriptorPath: checksum.DescriptorPath));
        Assert.Equal(1, checksumExit);

        NotesSnapshotFixture count = await CreateNotesSnapshotAsync(
            sequence: 6,
            snapshotId: "notes-count");
        Dictionary<string, long> wrongCounts =
            new(count.Descriptor.TableCounts, StringComparer.Ordinal)
            {
                ["notes"] = 2,
            };
        await WriteDescriptorAsync(
            count.DescriptorPath,
            count.Descriptor with { TableCounts = wrongCounts });
        int countExit = await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            Path.Combine(_tempDir, "bad-count"),
            "x",
            Force: false,
            SnapshotDbPath: count.DatabasePath,
            SnapshotDescriptorPath: count.DescriptorPath));
        Assert.Equal(1, countExit);
    }

    [Fact]
    public async Task SnapshotPublicationRejectsStaleSequenceAndKeepsCurrentSite()
    {
        NotesSnapshotFixture newer = await CreateNotesSnapshotAsync(
            sequence: 9,
            snapshotId: "notes-newer");
        NotesSnapshotFixture older = await CreateNotesSnapshotAsync(
            sequence: 8,
            snapshotId: "notes-older");
        string outDir = Path.Combine(_tempDir, "monotonic-site");

        Assert.Equal(0, await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            outDir,
            "x",
            Force: false,
            SnapshotDbPath: newer.DatabasePath,
            SnapshotDescriptorPath: newer.DescriptorPath)));
        Assert.Equal(1, await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            outDir,
            "x",
            Force: false,
            SnapshotDbPath: older.DatabasePath,
            SnapshotDescriptorPath: older.DescriptorPath)));

        SiteBuildManifest manifest = SiteBuildManifest.Read(Path.Combine(
            outDir,
            SiteBuildManifest.FileName));
        Assert.Equal("notes-newer", manifest.SnapshotId);
        Assert.Equal(9, manifest.SnapshotSequence);
    }

    [Fact]
    public async Task CommittedSnapshotRemainsSuccessfulWhenPrivateCleanupFails()
    {
        NotesSnapshotFixture snapshot = await CreateNotesSnapshotAsync(
            sequence: 10,
            snapshotId: "notes-cleanup-warning");
        string outDir = Path.Combine(_tempDir, "cleanup-warning-site");
        StringWriter stdout = new();
        StringWriter stderr = new();
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            int exit = await ReportRunner.RunAsync(
                new ReportOptions(
                    CliOptions.DefaultDb,
                    outDir,
                    "x",
                    Force: false,
                    SnapshotDbPath: snapshot.DatabasePath,
                    SnapshotDescriptorPath: snapshot.DescriptorPath),
                new NotesSiteCleanupHooks(
                    async privateSnapshot =>
                    {
                        await privateSnapshot.DisposeAsync();
                        throw new IOException("simulated private snapshot cleanup failure");
                    }));

            Assert.Equal(0, exit);
            Assert.Contains(snapshot.Descriptor.SnapshotId, stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("Warning: deferred cleanup", stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(
                "publication failed",
                stderr.ToString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(outDir, SiteBuildManifest.FileName)));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task SnapshotInputsInsideOutputAndPublisherControlPathsAreRejected()
    {
        string containedOut = Path.Combine(_tempDir, "contained-notes");
        Directory.CreateDirectory(containedOut);
        NotesSnapshotFixture contained = await CreateNotesSnapshotAsync(
            sequence: 11,
            snapshotId: "notes-contained");
        string containedDatabase = Path.Combine(
            containedOut,
            Path.GetFileName(contained.DatabasePath));
        string containedDescriptor = containedDatabase + ".json";
        File.Move(contained.DatabasePath, containedDatabase);
        File.Move(contained.DescriptorPath, containedDescriptor);

        int containedExit = await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            containedOut,
            "x",
            Force: false,
            SnapshotDbPath: containedDatabase,
            SnapshotDescriptorPath: containedDescriptor));

        Assert.Equal(1, containedExit);
        Assert.True(File.Exists(containedDatabase));
        Assert.True(File.Exists(containedDescriptor));
        Assert.False(File.Exists(Path.Combine(containedOut, "index.html")));

        NotesSnapshotFixture control = await CreateNotesSnapshotAsync(
            sequence: 12,
            snapshotId: "notes-control");
        string controlOut = Path.Combine(_tempDir, "control-notes");
        string controlDescriptor =
            Path.Combine(_tempDir, ".control-notes.publish-state.json");
        File.Move(control.DescriptorPath, controlDescriptor);

        int controlExit = await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            controlOut,
            "x",
            Force: false,
            SnapshotDbPath: control.DatabasePath,
            SnapshotDescriptorPath: controlDescriptor));

        Assert.Equal(1, controlExit);
        Assert.True(File.Exists(control.DatabasePath));
        Assert.True(File.Exists(controlDescriptor));
        Assert.False(Directory.Exists(controlOut));
    }

    [Fact]
    public async Task SnapshotWithForbiddenInternalColumnIsRejected()
    {
        NotesSnapshotFixture snapshot = await CreateNotesSnapshotAsync(
            sequence: 10,
            snapshotId: "notes-secret");
        await using (SqliteConnection connection = new(
            $"Data Source={snapshot.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE notes ADD COLUMN OperationToken TEXT";
            await command.ExecuteNonQueryAsync();
        }

        await using FileStream stream = File.OpenRead(snapshot.DatabasePath);
        AuthoringSnapshotDescriptor refreshed = snapshot.Descriptor with
        {
            Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream))
                .ToLowerInvariant(),
            SizeBytes = new FileInfo(snapshot.DatabasePath).Length,
        };
        await WriteDescriptorAsync(snapshot.DescriptorPath, refreshed);

        int exit = await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            Path.Combine(_tempDir, "secret-site"),
            "x",
            Force: false,
            SnapshotDbPath: snapshot.DatabasePath,
            SnapshotDescriptorPath: snapshot.DescriptorPath));
        Assert.Equal(1, exit);
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "secret-site")));
    }

    [Fact]
    public async Task SnapshotWithSchemaV1ColumnDriftIsRejected()
    {
        NotesSnapshotFixture snapshot = await CreateNotesSnapshotAsync(
            sequence: 11,
            snapshotId: "notes-schema-drift");
        await using (SqliteConnection connection = new(
            $"Data Source={snapshot.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(
                connection,
                "ALTER TABLE note_tickets ADD COLUMN FutureColumn TEXT");
        }
        AuthoringSnapshotDescriptor refreshed = snapshot.Descriptor with
        {
            Sha256 = await ComputeHashAsync(snapshot.DatabasePath),
            SizeBytes = new FileInfo(snapshot.DatabasePath).Length,
        };
        await WriteDescriptorAsync(snapshot.DescriptorPath, refreshed);

        int exit = await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            Path.Combine(_tempDir, "schema-drift-site"),
            "x",
            Force: false,
            SnapshotDbPath: snapshot.DatabasePath,
            SnapshotDescriptorPath: snapshot.DescriptorPath));

        Assert.Equal(1, exit);
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "schema-drift-site")));
    }

    [Fact]
    public async Task SnapshotRejectsSidecarsBeforeValidation()
    {
        NotesSnapshotFixture snapshot = await CreateNotesSnapshotAsync(
            sequence: 12,
            snapshotId: "notes-sidecar");
        await File.WriteAllTextAsync(snapshot.DatabasePath + "-shm", "sidecar");

        int exit = await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            Path.Combine(_tempDir, "sidecar-site"),
            "x",
            Force: false,
            SnapshotDbPath: snapshot.DatabasePath,
            SnapshotDescriptorPath: snapshot.DescriptorPath));

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task LegacyAndSnapshotOwnershipRequiresForceAndPreservesSequence()
    {
        string legacyDb = Path.Combine(_tempDir, "ownership-legacy.db");
        using (BallotNotesDatabase db = new(
            legacyDb,
            NullLogger<BallotNotesDatabase>.Instance))
        {
            db.Initialize();
            Seed(db);
        }

        NotesSnapshotFixture snapshot = await CreateNotesSnapshotAsync(
            sequence: 14,
            snapshotId: "notes-ownership-14");
        NotesSnapshotFixture older = await CreateNotesSnapshotAsync(
            sequence: 13,
            snapshotId: "notes-ownership-13");
        string outDir = Path.Combine(_tempDir, "ownership-site");

        Assert.Equal(0, await ReportRunner.RunAsync(new ReportOptions(
            legacyDb,
            outDir,
            "Legacy",
            Force: false)));
        Assert.Equal(1, await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            outDir,
            "Snapshot",
            Force: false,
            SnapshotDbPath: snapshot.DatabasePath,
            SnapshotDescriptorPath: snapshot.DescriptorPath)));
        Assert.Equal(0, await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            outDir,
            "Snapshot",
            Force: true,
            SnapshotDbPath: snapshot.DatabasePath,
            SnapshotDescriptorPath: snapshot.DescriptorPath)));
        Assert.Equal(1, await ReportRunner.RunAsync(new ReportOptions(
            legacyDb,
            outDir,
            "Legacy again",
            Force: false)));
        Assert.Equal(0, await ReportRunner.RunAsync(new ReportOptions(
            legacyDb,
            outDir,
            "Legacy again",
            Force: true)));
        Assert.Equal(1, await ReportRunner.RunAsync(new ReportOptions(
            CliOptions.DefaultDb,
            outDir,
            "Older snapshot",
            Force: true,
            SnapshotDbPath: older.DatabasePath,
            SnapshotDescriptorPath: older.DescriptorPath)));
        Assert.False(File.Exists(Path.Combine(outDir, SiteBuildManifest.FileName)));
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
    public void Emit_App_Js_Ships_CopyHtml_Affordance()
    {
        string dbPath = Path.Combine(_tempDir, "copy-html.db");
        using (BallotNotesDatabase db = new(dbPath, NullLogger<BallotNotesDatabase>.Instance))
        {
            db.Initialize();
            Seed(db);
        }

        string outDir = Path.Combine(_tempDir, "copy-html-site");
        new NotesSpaEmitter(dbPath, "x").Emit(outDir);

        string appJs = File.ReadAllText(Path.Combine(outDir, "assets", "app.js"));
        Assert.Contains("copyHtmlButton", appJs);
        Assert.Contains("clipboardWrite", appJs);
        Assert.Contains("Copy HTML", appJs);

        string appCss = File.ReadAllText(Path.Combine(outDir, "assets", "app.css"));
        Assert.Contains(".copy-html", appCss);
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

    [Fact]
    public void Emit_Snapshot_Carries_Multi_WG_For_Datatype_Note()
    {
        string dbPath = Path.Combine(_tempDir, "multiwg.db");
        using (BallotNotesDatabase db = new(dbPath, NullLogger<BallotNotesDatabase>.Instance))
        {
            db.Initialize();
            Seed(db);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            NoteRecord dt = new()
            {
                NoteId = "hl7-fhir-datatype-datatypes",
                Type = "DataType",
                Name = "datatypes",
                RepoOwner = "HL7",
                RepoName = "fhir",
                RepoCategory = "FhirCore",
                WorkGroup = "Foo",
                WorkGroupCode = "foo",
                WorkGroupNames = "Foo;Bar",
                WorkGroupCodes = "foo;bar",
                GeneratedAt = now,
                SavedAt = now,
            };
            db.UpsertUnitEvidence(dt, [], [], []);
        }

        string outDir = Path.Combine(_tempDir, "multiwg-site");
        new NotesSpaEmitter(dbPath, "x").Emit(outDir);
        byte[] dbBytes = Decompress(Convert.FromBase64String(ExtractDbBlob(File.ReadAllText(Path.Combine(outDir, "index.html")))));

        string snapPath = Path.Combine(_tempDir, "multiwg-decoded.db");
        File.WriteAllBytes(snapPath, dbBytes);
        using SqliteConnection conn = new($"Data Source={snapPath};Pooling=False");
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT WorkGroupNames, WorkGroupCodes FROM notes WHERE Name='datatypes'";
        using SqliteDataReader reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Foo;Bar", reader.GetString(0));
        Assert.Equal("foo;bar", reader.GetString(1));

        // The SPA groups/filters/displays by the multi-WG set.
        string appJs = File.ReadAllText(Path.Combine(outDir, "assets", "app.js"));
        Assert.Contains("WorkGroupNames", appJs);
        Assert.Contains("wgNames", appJs);
    }

    [Fact]
    public void Emit_App_Js_Ships_Landing_WorkGroup_Lineage_Columns()
    {
        string dbPath = Path.Combine(_tempDir, "landing-wg.db");
        using (BallotNotesDatabase db = new(dbPath, NullLogger<BallotNotesDatabase>.Instance))
        {
            db.Initialize();
            Seed(db);
        }

        string outDir = Path.Combine(_tempDir, "landing-wg-site");
        new NotesSpaEmitter(dbPath, "x").Emit(outDir);

        string appJs = File.ReadAllText(Path.Combine(outDir, "assets", "app.js"));
        // Phase 3: two landing columns (code + name), discrepancy badge, and the
        // helpers/columns selected for both-column filtering.
        Assert.Contains("Listed WG", appJs);
        Assert.Contains("JIRA Artifact WG", appJs);
        Assert.Contains("wgDisagree", appJs);
        Assert.Contains("ListedWorkGroupNames", appJs);
        Assert.Contains("IndexWorkGroupNames", appJs);
        Assert.Contains("AppliedWorkGroupCodes", appJs);
        Assert.Contains("listedOf", appJs);
        Assert.Contains("indexOf", appJs);

        string appCss = File.ReadAllText(Path.Combine(outDir, "assets", "app.css"));
        Assert.Contains(".wg-warn", appCss);
    }

    [Fact]
    public void Emit_Snapshot_Carries_Lineage_Columns_When_Listed_Differs_From_Index()
    {
        string dbPath = Path.Combine(_tempDir, "lineage.db");
        using (BallotNotesDatabase db = new(dbPath, NullLogger<BallotNotesDatabase>.Instance))
        {
            db.Initialize();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            NoteRecord note = new()
            {
                NoteId = "hl7-fhir-artifact-account",
                Type = "Artifact",
                Name = "Account",
                RepoOwner = "HL7",
                RepoName = "fhir",
                RepoCategory = "FhirCore",
                WorkGroup = "FHIR Infrastructure (FHIR-I)",
                WorkGroupCode = "fhir",
                ListedWorkGroupNames = "FHIR Infrastructure (FHIR-I)",
                ListedWorkGroupCodes = "fhir",
                IndexWorkGroupNames = "Patient Administration (PA)",
                IndexWorkGroupCodes = "pa",
                AppliedWorkGroupNames = "Orders and Observations (OO)",
                AppliedWorkGroupCodes = "oo",
                GeneratedAt = now,
                SavedAt = now,
            };
            db.UpsertUnitEvidence(note, [], [], []);
        }

        string outDir = Path.Combine(_tempDir, "lineage-site");
        new NotesSpaEmitter(dbPath, "x").Emit(outDir);
        byte[] dbBytes = Decompress(Convert.FromBase64String(ExtractDbBlob(File.ReadAllText(Path.Combine(outDir, "index.html")))));

        string snapPath = Path.Combine(_tempDir, "lineage-decoded.db");
        File.WriteAllBytes(snapPath, dbBytes);
        using SqliteConnection conn = new($"Data Source={snapPath};Pooling=False");
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT ListedWorkGroupCodes, IndexWorkGroupCodes, AppliedWorkGroupCodes " +
            "FROM notes WHERE Name='Account'";
        using SqliteDataReader reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("fhir", reader.GetString(0));
        Assert.Equal("pa", reader.GetString(1));   // listed ≠ index → badge
        Assert.Equal("oo", reader.GetString(2));
    }

    [Fact]
    public void Emit_App_Js_Ships_Detail_And_Markdown_WorkGroup_Lineage_Rows()
    {
        string dbPath = Path.Combine(_tempDir, "detail-wg.db");
        using (BallotNotesDatabase db = new(dbPath, NullLogger<BallotNotesDatabase>.Instance))
        {
            db.Initialize();
            Seed(db);
        }

        string outDir = Path.Combine(_tempDir, "detail-wg-site");
        new NotesSpaEmitter(dbPath, "x").Emit(outDir);

        string appJs = File.ReadAllText(Path.Combine(outDir, "assets", "app.js"));
        // Phase 4: three detail-summary rows + the markdown export field rows.
        Assert.Contains("Listed workgroup", appJs);
        Assert.Contains("JIRA index workgroup", appJs);
        Assert.Contains("Applied-by workgroups", appJs);
        Assert.Contains("indexWgDetailNode", appJs);
        Assert.Contains("appliedOf", appJs);
        // The single legacy "Workgroup" detail row is gone (grouping still uses wgNames).
        Assert.DoesNotContain("kv('Workgroup'", appJs);
    }

    private static long Count(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static byte[] Decompress(byte[] gz)
    {
        using MemoryStream input = new(gz);
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        gzip.CopyTo(output);
        return output.ToArray();
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

    private async Task<NotesSnapshotFixture> CreateNotesSnapshotAsync(
        long sequence,
        string snapshotId)
    {
        string databasePath = Path.Combine(
            _tempDir,
            $"github-fhir-ballot-notes-{snapshotId}.db");
        string descriptorPath = databasePath + ".json";
        string runId = $"run-{snapshotId}";
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        await using SqliteConnection connection = new(
            $"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        BallotNotesDatabase.EnsureSchema(connection);
        await ExecuteAsync(connection,
            """
            CREATE TABLE authoring_snapshot_provenance(SnapshotId TEXT, ProcessorKind TEXT, RunId TEXT, AuthoringEpoch INTEGER, Sequence INTEGER, SchemaVersion INTEGER, ItemCount INTEGER, ReceiptCount INTEGER, TableCountsJson TEXT, CreatedAt TEXT);

            INSERT INTO authoring_runs(
                Id, ProcessorKind, AuthoringEpoch, Status, DatabaseOnly, TotalItems,
                CreatedAt, StartedAt, CompletedAt, SnapshotId)
            VALUES(@runId, 'github-fhir-ballot-notes', 1, 'finalizing', 0, 1,
                @createdAt, @createdAt, NULL, @snapshotId);
            INSERT INTO authoring_run_items(
                Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision, Status,
                AcceptedReceiptId, AttemptCount, CreatedAt, StartedAt, CompletedAt)
            VALUES('item-1', @runId, 'note-1', 'Artifact', 'evidence-1', 'complete',
                'receipt-1', 1, @createdAt, @createdAt, @createdAt);
            INSERT INTO authoring_result_receipts(
                Id, OperationId, RunId, RunItemId, BusinessKey, ContentHash,
                ExpectedSourceRevision, ObservedSourceRevision, AuthoringEpoch, PersistedAt)
            VALUES('receipt-1', 'operation-1', @runId, 'item-1', 'note-1',
                'hash', 'evidence-1', 'evidence-1', 1, @createdAt);
            INSERT INTO notes(
                NoteId, Type, Name, RepoOwner, RepoName, RepoCategory, WorkGroup,
                WorkGroupCode, WorkGroupNames, WorkGroupCodes, ListedWorkGroupNames,
                ListedWorkGroupCodes, IndexWorkGroupNames, IndexWorkGroupCodes,
                AppliedWorkGroupNames, AppliedWorkGroupCodes, SinceSha, SinceShortSha,
                HeadSha, HeadShortSha, CurrentHydrationExecutionId, CurrentEvidenceHash,
                CurrentEvidenceRevision, WindowLabel, CommitsInWindow, TicketsAttributed,
                NeedsNote, CurrentBallotNoteHtml, CurrentNoteIsAuguryGenerated,
                PreservedHandAuthoredHtml, ProposedBallotNoteHtml, RollupSummaryMarkdown,
                NotesForReviewerMarkdown, SourceFilesNote, ProseHydrationExecutionId,
                ProseEvidenceRevision, CurrentAuthoringOperationId,
                ProseVerificationStatus, HydratedAt, AuthoredAt, GeneratedAt, SavedAt)
            VALUES('note-1', 'Artifact', 'Observation', 'HL7', 'fhir', '', '', '',
                '', '', '', '', '', '', '', '', 'aaaa', 'aaaa', 'bbbb', 'bbbb',
                'execution-1', 'evidence-hash', 'evidence-1', '', 1, 1, 'yes',
                '', 0, '', '<p>Proposed note</p>', 'Rollup', 'Reviewer notes', '',
                'execution-1', 'evidence-1', 'operation-1', 'receipt-backed',
                @createdAt, @createdAt, @createdAt, @createdAt);
            INSERT INTO note_source_files(
                Id, NoteId, Path, Role, TouchedInWindow, FileOrder)
            VALUES('source-1', 'note-1', 'source.html', 'source', 1, 1);
            INSERT INTO note_commits(
                Id, NoteId, Sha, ShortSha, AuthorName, AuthorDate, Subject,
                WebUrl, TicketKeys, CommitOrder)
            VALUES('commit-1', 'note-1', 'abcdef', 'abcdef', 'Author',
                @createdAt, 'Subject', '', 'FHIR-1', 1);
            INSERT INTO note_tickets(
                Id, NoteId, TicketKey, Title, Resolution, WorkGroup, Specification,
                Url, ChangeImpact, ChangeCategory, IssueType, RelatedTicketKeys,
                CommitCount, TicketOrder)
            VALUES('ticket-1', 'note-1', 'FHIR-1', 'Title', 'Persuasive',
                'FHIR Infrastructure', 'FHIR', '', 'Non-substantive', '',
                'Change Request', '', 1, 1);
            INSERT INTO notes_hydration_executions(
                Id, RunKey, RepoOwner, RepoName, RepoCategory, SinceSha, SinceShortSha,
                HeadSha, HeadShortSha, WindowLabel, Status, MutationRunId,
                MutationLeaseId, UnitsTotal, UnitsHydrated, CommitsInWindow,
                TicketsAttributed, IsCutoverBaseline, StartedAt, CompletedAt, Error)
            VALUES('execution-1', 'run-key', 'HL7', 'fhir', '', 'aaaa', 'aaaa',
                'bbbb', 'bbbb', '', 'completed', '', '', 1, 1, 1, 1, 0,
                @createdAt, @createdAt, '');
            INSERT INTO notes_hydration_run_items(
                Id, ExecutionId, NoteId, Type, ItemOrder, Status, EvidenceHash,
                EvidenceRevision, HydratedAt, Error)
            VALUES('hydration-item-1', 'execution-1', 'note-1', 'Artifact', 1,
                'completed', 'evidence-hash', 'evidence-1', @createdAt, '');
            """,
            ("@runId", runId),
            ("@snapshotId", snapshotId),
            ("@createdAt", createdAt.ToString("O")));

        string[] tables =
        [
            "notes",
            "note_source_files",
            "note_commits",
            "note_tickets",
            "note_structural_changes",
            "note_extension_refs",
            "notes_hydration_executions",
            "notes_hydration_run_items",
        ];
        await SanitizeNotesSnapshotSchemaAsync(
            connection,
            [
                "authoring_runs",
                "authoring_run_items",
                "authoring_result_receipts",
                "authoring_snapshot_provenance",
                .. tables,
            ]);
        Dictionary<string, long> counts = [];
        foreach (string table in tables)
        {
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
            counts[table] = Convert.ToInt64(await count.ExecuteScalarAsync());
        }
        await ExecuteAsync(connection,
            """
            INSERT INTO authoring_snapshot_provenance
            VALUES(@snapshotId, 'github-fhir-ballot-notes', @runId, 1, @sequence, 1, 1, 1, @counts, @createdAt)
            """,
            ("@snapshotId", snapshotId),
            ("@runId", runId),
            ("@sequence", sequence),
            ("@counts", JsonSerializer.Serialize(counts)),
            ("@createdAt", createdAt.ToString("O")));
        await connection.CloseAsync();

        await using FileStream stream = new(
            databasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        string sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream))
            .ToLowerInvariant();
        AuthoringSnapshotDescriptor descriptor = new(
            "github-fhir-ballot-notes",
            runId,
            snapshotId,
            1,
            sequence,
            1,
            sha256,
            new FileInfo(databasePath).Length,
            1,
            1,
            counts,
            Path.GetFileName(databasePath),
            createdAt);
        await WriteDescriptorAsync(descriptorPath, descriptor);
        return new NotesSnapshotFixture(databasePath, descriptorPath, descriptor);
    }

    private static Task WriteDescriptorAsync(
        string path,
        AuthoringSnapshotDescriptor descriptor)
        => File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                descriptor,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true,
                }));

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream))
            .ToLowerInvariant();
    }

    private static async Task SanitizeNotesSnapshotSchemaAsync(
        SqliteConnection connection,
        IReadOnlyCollection<string> publicTables)
    {
        Dictionary<string, string[]> selectedColumns =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["authoring_runs"] =
                [
                    "Id", "ProcessorKind", "AuthoringEpoch", "Status", "DatabaseOnly",
                    "TotalItems", "CreatedAt", "StartedAt", "CompletedAt", "SnapshotId",
                ],
                ["authoring_run_items"] =
                [
                    "Id", "RunId", "BusinessKey", "ItemKind", "ExpectedSourceRevision",
                    "Status", "AcceptedReceiptId", "AttemptCount", "CreatedAt", "StartedAt",
                    "CompletedAt",
                ],
                ["authoring_result_receipts"] =
                [
                    "Id", "OperationId", "RunId", "RunItemId", "BusinessKey",
                    "ContentHash", "ExpectedSourceRevision", "ObservedSourceRevision",
                    "AuthoringEpoch", "PersistedAt",
                ],
                ["notes_hydration_executions"] =
                [
                    "Id", "RunKey", "RepoOwner", "RepoName", "RepoCategory", "SinceSha",
                    "SinceShortSha", "HeadSha", "HeadShortSha", "WindowLabel", "Status",
                    "UnitsTotal", "UnitsHydrated", "CommitsInWindow", "TicketsAttributed",
                    "IsCutoverBaseline", "StartedAt", "CompletedAt",
                ],
                ["notes_hydration_run_items"] =
                [
                    "Id", "ExecutionId", "NoteId", "Type", "ItemOrder", "Status",
                    "EvidenceHash", "EvidenceRevision", "HydratedAt",
                ],
            };

        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF");
        List<(string Type, string Name)> objects = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT type, name
                FROM sqlite_master
                WHERE type IN ('table', 'view', 'trigger')
                  AND name NOT LIKE 'sqlite_%'
                ORDER BY type, name
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                objects.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach ((string type, string name) in objects.Where(item => item.Type != "table"))
        {
            await ExecuteAsync(
                connection,
                $"DROP {type.ToUpperInvariant()} IF EXISTS \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
        }
        foreach ((string _, string name) in objects.Where(item => item.Type == "table"))
        {
            if (!publicTables.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                await ExecuteAsync(
                    connection,
                    $"DROP TABLE IF EXISTS \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
            }
        }

        foreach ((string table, string[] columns) in selectedColumns)
        {
            string replacement = $"__snapshot_{Guid.NewGuid():N}";
            string columnList = string.Join(
                ", ",
                columns.Select(column =>
                    $"\"{column.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
            await ExecuteAsync(
                connection,
                $"""
                CREATE TABLE "{replacement}" AS
                SELECT {columnList} FROM "{table}";
                DROP TABLE "{table}";
                ALTER TABLE "{replacement}" RENAME TO "{table}";
                """);
        }
        await ExecuteAsync(connection, "VACUUM; PRAGMA foreign_keys = ON");
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private sealed record NotesSnapshotFixture(
        string DatabasePath,
        string DescriptorPath,
        AuthoringSnapshotDescriptor Descriptor);
}
