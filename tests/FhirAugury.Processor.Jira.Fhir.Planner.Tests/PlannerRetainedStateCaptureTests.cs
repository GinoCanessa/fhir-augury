using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Maintenance;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannerRetainedStateCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_PreservesCompleteOriginalFileSet(bool existingOwnerLock)
    {
        using CaptureFixture fixture = new();
        Directory.CreateDirectory(Path.Combine(fixture.Original, "empty"));
        File.WriteAllBytes(Path.Combine(fixture.Original, "opaque.bin"), [0, 255, 0, 1, 128]);
        string extra = Path.Combine(fixture.Root, "extra");
        Directory.CreateDirectory(extra);
        File.WriteAllText(Path.Combine(extra, "retained.txt"), "unknown retained member\0\n|");
        fixture.Settings = fixture.Settings with { RetainedRoots = [extra] };
        byte[] lockBytes = [8, 0, 7, 255, 4];
        if (existingOwnerLock)
        {
            File.WriteAllBytes(fixture.Database + ".owner.lock", lockBytes);
        }
        Dictionary<string, string> before = fixture.OriginalHashes();
        string extraHash = CaptureFixture.Hash(Path.Combine(extra, "retained.txt"));
        PlannerRetainedStateManifest manifest = await new PlannerRetainedStateCapture().CaptureAsync(fixture.Settings);

        fixture.AssertOriginals(before);
        Assert.Equal(extraHash, CaptureFixture.Hash(Path.Combine(extra, "retained.txt")));
        Assert.Equal(existingOwnerLock, manifest.OwnerLock.Preexisted);
        Assert.Equal(!existingOwnerLock, manifest.OwnerLock.CreatedByCapture);
        Assert.Equal(existingOwnerLock ? lockBytes : [], File.ReadAllBytes(fixture.Database + ".owner.lock"));
        Assert.Null(manifest.SettingsPaths.PreCutoverBackup);
        Assert.False(manifest.SettingsPaths.Snapshots.Existed);
        Assert.False(Directory.Exists(fixture.Settings.Snapshots));
        Assert.Contains(manifest.Directories, directory => directory.RelativePath == "empty");
        Assert.Contains(manifest.Artifacts, artifact => artifact.RelativePath == "opaque.bin");
        Assert.Contains(manifest.Artifacts, artifact => artifact.RelativePath == "retained.txt");
        foreach (PlannerRetainedArtifact artifact in manifest.Artifacts)
        {
            Assert.Equal(CaptureFixture.Hash(artifact.OriginalPath),
                CaptureFixture.Hash(fixture.BundlePath(artifact.RawPath)));
            Assert.NotEqual(artifact.Identity, PlannerRetainedStatePaths.InspectFile(fixture.BundlePath(artifact.RawPath)));
        }
        PlannerRetainedDatabaseInventory database = Assert.Single(manifest.Databases);
        PlannerRetainedTable source = Assert.Single(database.Tables, table => table.Name == "jira_processing_source_tickets");
        Assert.DoesNotContain(source.Columns, column => column.Name == "CompletionId");
        Assert.Contains("authoring_runs", database.AbsentLegacyTables);
        Assert.All(manifest.Sidecars, sidecar => Assert.Null(sidecar.ArtifactId));
        Assert.Equal(3, manifest.Sidecars.Count);
        Assert.Equal("ok", fixture.ScalarString(fixture.BundlePath(database.SafetyPath), "PRAGMA integrity_check"));
        Assert.Equal("legacy-id", fixture.ScalarString(fixture.BundlePath(database.SafetyPath),
            "SELECT Id FROM jira_processing_source_tickets"));
        using PlannerRetainedVerifiedCapture verified = await fixture.OpenVerifiedAsync();
        Assert.Throws<IOException>(() =>
        {
            using FileStream writer = new(fixture.BundlePath("safety/planner.db"),
                FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        });
    }

    [Fact]
    public async Task Capture_IncludesCommittedWalWithoutTouchingOriginalSidecars()
    {
        using CaptureFixture fixture = new(createDatabase: false);
        fixture.SeedCommittedWal();
        Dictionary<string, string> before = fixture.OriginalHashes();
        PlannerRetainedStateManifest manifest = await new PlannerRetainedStateCapture().CaptureAsync(fixture.Settings);

        fixture.AssertOriginals(before);
        Assert.Equal("committed WAL value", fixture.ScalarString(fixture.BundlePath("safety/planner.db"),
            "SELECT Value FROM wal_only WHERE Id = 1"));
        Assert.Contains(manifest.Sidecars, sidecar => sidecar.Suffix == "-wal" && sidecar.ArtifactId is not null);
        Assert.Contains(manifest.Sidecars, sidecar => sidecar.Suffix == "-shm" && sidecar.ArtifactId is not null);
        PlannerRetainedArtifact shm = Assert.Single(manifest.Artifacts, artifact => artifact.Kind == "sqlite-shm");
        Assert.Equal(before[fixture.Database + "-shm"], CaptureFixture.Hash(fixture.BundlePath(shm.RawPath)));
        Assert.False(File.Exists(fixture.BundlePath("safety/planner.db-wal")));
        Assert.False(File.Exists(fixture.BundlePath("safety/planner.db-shm")));
        Assert.DoesNotContain(manifest.Databases[0].Tables.Single(table => table.Name == "jira_processing_source_tickets").Columns,
            column => column.Name == "CompletionId");
    }

    [Fact]
    public async Task Capture_RecordsAbsentConfiguredBackupAndExternalSnapshotRootWithoutCreatingThem()
    {
        using CaptureFixture fixture = new();
        fixture.Settings = fixture.Settings with
        {
            Database = fixture.Database.Replace('\\', '/'),
            PreCutoverBackup = Path.Combine(fixture.Root, "not-created-backups", "pre-cutover.db"),
            Snapshots = Path.Combine(fixture.Root, "not-created-snapshots"),
            ActivateRunBackedAuthoring = true,
        };
        PlannerRetainedStateManifest manifest = await new PlannerRetainedStateCapture().CaptureAsync(fixture.Settings);
        Assert.False(manifest.SettingsPaths.PreCutoverBackup!.Existed);
        Assert.False(manifest.SettingsPaths.Snapshots.Existed);
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Settings.PreCutoverBackup)));
        Assert.False(Directory.Exists(fixture.Settings.Snapshots));
        using PlannerRetainedVerifiedCapture verified = await fixture.OpenVerifiedAsync();
        Assert.Equal(3, verified.Manifest.Roots.Count);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("parent")]
    [InlineData("directory")]
    public async Task Capture_RefusesMissingDatabaseBeforeCreatingOwnershipFiles(string missing)
    {
        using CaptureFixture fixture = new(createDatabase: false);
        if (missing == "parent")
        {
            fixture.Settings = fixture.Settings with { Database = Path.Combine(fixture.Root, "not-created", "planner.db") };
        }
        else if (missing == "directory")
        {
            Directory.CreateDirectory(fixture.Database);
        }
        PlannerRetainedStateException error = await Assert.ThrowsAsync<PlannerRetainedStateException>(
            () => new PlannerRetainedStateCapture().CaptureAsync(fixture.Settings));
        Assert.Equal("database-missing-or-not-regular", error.Category);
        Assert.False(File.Exists(fixture.Settings.Database + ".owner.lock"));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "not-created")));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("database-writer")]
    [InlineData("unknown-writer")]
    [InlineData("sidecar-writer")]
    public async Task Capture_RefusesCompetingOwnerOrWriter(string contender)
    {
        using CaptureFixture fixture = new();
        File.WriteAllBytes(fixture.Database + ".owner.lock", [1, 3, 5, 7]);
        string unknown = Path.Combine(fixture.Original, "opaque.txt");
        File.WriteAllText(unknown, "immutable fixture");
        if (contender == "sidecar-writer")
        {
            File.WriteAllBytes(fixture.Database + "-wal", []);
        }
        Dictionary<string, string> before = fixture.OriginalHashes();
        using PlannerDatabase owner = new(fixture.Database, NullLogger<PlannerDatabase>.Instance, readOnly: true);
        using FileStream? writer = contender == "owner" ? null : new(
            contender == "unknown-writer" ? unknown : contender == "sidecar-writer" ? fixture.Database + "-wal" : fixture.Database,
            FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        if (contender == "owner")
        {
            owner.AcquireStartupOwnership();
        }
        PlannerRetainedStateException error = await Assert.ThrowsAsync<PlannerRetainedStateException>(
            () => new PlannerRetainedStateCapture().CaptureAsync(fixture.Settings));
        Assert.Equal("owner-or-writer-busy", error.Category);
        Assert.Equal(20, error.ExitCode);
        Assert.False(File.Exists(fixture.BundlePath("capture.complete.json")));
        writer?.Dispose();
        owner.Dispose();
        fixture.AssertOriginals(before);
    }

    [Fact]
    public async Task Capture_ReadHandlesExcludeNewWritersAndOwnershipThroughFinalComparison()
    {
        using CaptureFixture fixture = new();
        int checks = 0;
        await new PlannerRetainedStateCapture(stage =>
        {
            if (stage is "files-frozen" or "before-completion")
            {
                Assert.Throws<IOException>(() =>
                {
                    using FileStream writer = new(fixture.Database, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                });
                using PlannerDatabase otherOwner = new(fixture.Database, NullLogger<PlannerDatabase>.Instance, readOnly: true);
                Assert.Throws<InvalidOperationException>(otherOwner.AcquireStartupOwnership);
                checks++;
            }
        }).CaptureAsync(fixture.Settings);
        Assert.Equal(2, checks);
        using PlannerDatabase nextOwner = new(fixture.Database, NullLogger<PlannerDatabase>.Instance, readOnly: true);
        nextOwner.AcquireStartupOwnership();
    }

    [Fact]
    public async Task Capture_RefusesExistingDeleteHandleWithoutDeletingAnyOriginal()
    {
        using CaptureFixture fixture = new();
        Dictionary<string, string> before = fixture.OriginalHashes();
        using SafeFileHandle deletion = CreateFileNative(fixture.Database, 0x00010000,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3, 0, IntPtr.Zero);
        Assert.False(deletion.IsInvalid);
        PlannerRetainedStateException error = await Assert.ThrowsAsync<PlannerRetainedStateException>(
            () => new PlannerRetainedStateCapture().CaptureAsync(fixture.Settings));
        Assert.Equal("owner-or-writer-busy", error.Category);
        Assert.False(File.Exists(fixture.BundlePath("capture.complete.json")));
        deletion.Dispose();
        fixture.AssertOriginals(before);
    }

    [Theory]
    [InlineData("files-frozen")]
    [InlineData("raw-copied")]
    [InlineData("before-completion")]
    public async Task Capture_RefusesChangingMembership(string changedAt)
    {
        using CaptureFixture fixture = new();
        bool changed = false;
        PlannerRetainedStateException error = await Assert.ThrowsAsync<PlannerRetainedStateException>(() =>
            new PlannerRetainedStateCapture(stage =>
            {
                if (stage == changedAt)
                {
                    File.WriteAllText(Path.Combine(fixture.Original, "new-member"), "writer raced membership");
                    changed = true;
                }
            }).CaptureAsync(fixture.Settings));
        Assert.True(changed);
        Assert.Equal("membership-changed", error.Category);
        Assert.False(File.Exists(fixture.BundlePath("capture.complete.json")));
        Assert.True(File.Exists(fixture.BundlePath("capture.incomplete.json")));
    }

    [Theory]
    [InlineData("-journal", "unsupported-rollback-journal")]
    [InlineData("-mj123456789", "unsupported-super-journal")]
    [InlineData("-sj123456789", "unsupported-super-journal")]
    public async Task Capture_RefusesNonemptyRollbackOrSuperJournal(string suffix, string category)
    {
        using CaptureFixture fixture = new();
        File.WriteAllBytes(fixture.Database + suffix, [0, 1, 3, 7, 9]);
        Dictionary<string, string> before = fixture.OriginalHashes();
        bool sqliteReached = false;
        PlannerRetainedStateException error = await Assert.ThrowsAsync<PlannerRetainedStateException>(() =>
            new PlannerRetainedStateCapture(stage => sqliteReached |= stage == "before-sqlite").CaptureAsync(fixture.Settings));
        Assert.Equal(category, error.Category);
        Assert.False(sqliteReached);
        fixture.AssertOriginals(before);
        Assert.True(File.Exists(fixture.BundlePath("raw.catalog.json")));
        Assert.Equal(before[fixture.Database + suffix],
            CaptureFixture.Hash(Directory.GetFiles(fixture.BundlePath("raw"), "*" + suffix, SearchOption.AllDirectories).Single()));
        Assert.False(File.Exists(fixture.BundlePath("capture.complete.json")));
        Assert.False(Directory.Exists(fixture.BundlePath("normalization")));
    }

    [Theory]
    [InlineData("orphan-wal")]
    [InlineData("malformed-wal")]
    [InlineData("not-a-database")]
    [InlineData("orphan-shm")]
    public async Task Capture_RefusesUnclassifiedFamiliesBeforeSqlite(string defect)
    {
        using CaptureFixture fixture = new();
        switch (defect)
        {
            case "orphan-wal": File.WriteAllBytes(Path.Combine(fixture.Original, "orphan.db-wal"), []); break;
            case "orphan-shm": File.WriteAllBytes(Path.Combine(fixture.Original, "orphan.db-shm"), [0]); break;
            case "malformed-wal": File.WriteAllBytes(fixture.Database + "-wal", [1, 2, 3]); break;
            case "not-a-database": File.WriteAllText(Path.Combine(fixture.Original, "unexpected.db"), "not SQLite"); break;
        }
        bool sqliteReached = false;
        PlannerRetainedStateException error = await Assert.ThrowsAsync<PlannerRetainedStateException>(() =>
            new PlannerRetainedStateCapture(stage => sqliteReached |= stage == "before-sqlite").CaptureAsync(fixture.Settings));
        Assert.Equal("unclassified-sqlite-family", error.Category);
        Assert.False(sqliteReached);
        Assert.True(File.Exists(fixture.BundlePath("raw.catalog.json")));
    }

    [Fact]
    public async Task Capture_PreservesDistinctPreCutoverAndSnapshotBytes()
    {
        using CaptureFixture fixture = new(currentSchema: true);
        fixture.SeedCompleteGraph();
        await fixture.SeedSnapshotAsync();
        fixture.SeedDistinctBackup();
        Dictionary<string, string> before = fixture.OriginalHashes();
        PlannerRetainedStateManifest manifest = await new PlannerRetainedStateCapture().CaptureAsync(fixture.Settings);

        fixture.AssertOriginals(before);
        Assert.True(manifest.SettingsPaths.PreCutoverBackup!.Existed);
        Assert.Equal(3, manifest.Databases.Count);
        Assert.Equal(3, manifest.Databases.Select(database => database.SafetyPath).Distinct(StringComparer.Ordinal).Count());
        PlannerRetainedArtifact backup = Assert.Single(manifest.Artifacts, artifact =>
            artifact.OriginalPath == fixture.Settings.PreCutoverBackup);
        Assert.NotEqual(backup.Sha256, manifest.Artifacts.Single(artifact => artifact.OriginalPath == fixture.Database).Sha256);
        Assert.Equal(backup.Sha256, CaptureFixture.Hash(fixture.BundlePath(backup.RawPath)));
        Assert.Contains(manifest.SnapshotReferences, reference =>
            reference.Status == "ready" && reference.FinalizedTempAbsent && reference.DescriptorArtifactIds.Count == 1);
        Assert.Contains(manifest.Databases.SelectMany(database => database.Checks),
            check => check.Name == "verified-snapshot-projection" && check.Status == "passed");
        Assert.All(manifest.Databases.SelectMany(database => database.Checks), check => Assert.NotEqual("failed", check.Status));
        Assert.Equal("receipt-1", fixture.ScalarString(fixture.BundlePath("safety/planner.db"),
            "SELECT CompletionId FROM jira_processing_source_tickets"));
        Assert.Equal("maintenance:old-generation:fixture", fixture.ScalarString(fixture.BundlePath("safety/planner.db"),
            "SELECT RunId FROM authoring_mutation_fences"));
    }

    [Fact]
    public async Task Inventory_PreservesAllStorageClassesRowsAndUnknownTables()
    {
        using CaptureFixture fixture = new();
        using (SqliteConnection connection = CaptureFixture.Open(fixture.Database))
        {
            CaptureFixture.Execute(connection, """
                CREATE TABLE unknown_values(Id INTEGER PRIMARY KEY AUTOINCREMENT, Value);
                INSERT INTO unknown_values(Value) VALUES(NULL), (''), (X''), (0), (-9223372036854775808),
                    (9223372036854775807), (1.2345678901234567), (X'00FF80007C0A'), ('a|b'), ('a');
                INSERT INTO unknown_values(Value) VALUES(CAST(X'6100620A7C' AS TEXT)), (CAST(X'80FF' AS TEXT));
                CREATE TABLE duplicate_rows(Value, Other);
                INSERT INTO duplicate_rows VALUES('same', X'0001'), ('same', X'0001'), ('same', X'0001');
                CREATE TABLE composite_key(A TEXT COLLATE NOCASE, B INTEGER, C BLOB, PRIMARY KEY(A, B)) WITHOUT ROWID;
                INSERT INTO composite_key VALUES('b', 1, X'00'), ('A', 2, X'');
                CREATE TABLE generated_values(Id INTEGER PRIMARY KEY, Value TEXT, Generated TEXT GENERATED ALWAYS AS (Value || ' suffix') STORED);
                INSERT INTO generated_values(Id, Value) VALUES(9, 'generated');
                CREATE TABLE "unknown_ø"(Id INTEGER PRIMARY KEY, Value TEXT);
                CREATE TABLE "unknown_Ø"(Id INTEGER PRIMARY KEY, Value TEXT);
                INSERT INTO "unknown_ø" VALUES(1, 'lower');
                INSERT INTO "unknown_Ø" VALUES(1, 'upper');
                CREATE VIRTUAL TABLE fixture_fts USING fts5(body);
                INSERT INTO fixture_fts(rowid, body) VALUES(13, 'retained search content');
                CREATE INDEX fixture_expression ON unknown_values(length(Value)) WHERE Value IS NOT NULL;
                CREATE VIEW fixture_view AS SELECT Id, Value FROM unknown_values;
                CREATE TRIGGER protect_source BEFORE UPDATE ON jira_processing_source_tickets
                BEGIN SELECT RAISE(ABORT, 'capture must not update'); END;
                ANALYZE;
                """);
            byte[] large = new byte[2 * 1024 * 1024];
            for (int index = 0; index < large.Length; index++)
            {
                large[index] = (byte)(index % 251);
            }
            CaptureFixture.Execute(connection, "INSERT INTO unknown_values(Value) VALUES(@large)", ("@large", large));
        }
        Dictionary<string, string> before = fixture.OriginalHashes();
        PlannerRetainedStateManifest manifest = await new PlannerRetainedStateCapture().CaptureAsync(fixture.Settings);
        fixture.AssertOriginals(before);
        PlannerRetainedDatabaseInventory inventory = Assert.Single(manifest.Databases);
        PlannerRetainedTable values = Assert.Single(inventory.Tables, table => table.Name == "unknown_values");
        List<TypedRow> rows = ReadRows(fixture.BundlePath(values.Rows.Path));
        Assert.Equal(13, rows.Count);
        Assert.Equal(5, rows[0].Values[2].Type); // SQLite NULL
        Assert.Equal(3, rows[1].Values[2].Type); // TEXT, including empty versus BLOB
        Assert.Empty(rows[1].Values[2].Bytes);
        Assert.Equal(4, rows[2].Values[2].Type);
        Assert.Empty(rows[2].Values[2].Bytes);
        Assert.Equal(long.MinValue, BitConverter.ToInt64(rows[4].Values[2].Bytes));
        Assert.Equal(long.MaxValue, BitConverter.ToInt64(rows[5].Values[2].Bytes));
        Assert.Equal(BitConverter.DoubleToInt64Bits(1.2345678901234567), BitConverter.ToInt64(rows[6].Values[2].Bytes));
        Assert.Equal([0, 255, 128, 0, 124, 10], rows[7].Values[2].Bytes);
        Assert.Equal(Encoding.UTF8.GetBytes("a\0b\n|"), rows[10].Values[2].Bytes);
        Assert.Equal([128, 255], rows[11].Values[2].Bytes);
        Assert.Equal(2 * 1024 * 1024, rows[12].Values[2].Bytes.Length);
        PlannerRetainedTable duplicate = Assert.Single(inventory.Tables, table => table.Name == "duplicate_rows");
        Assert.Equal(3, duplicate.Rows.RowCount);
        Assert.Equal(3, ReadRows(fixture.BundlePath(duplicate.Rows.Path)).Select(row =>
            BitConverter.ToInt64(row.Values[0].Bytes)).Distinct().Count());
        Assert.Contains(inventory.Tables, table => table.Name == "sqlite_sequence" && table.Rows.RowCount != 0);
        Assert.Contains(inventory.Tables, table => table.Name == "sqlite_stat1");
        Assert.Contains(inventory.Tables, table => table.Name == "unknown_ø" && table.Rows.RowCount == 1);
        Assert.Contains(inventory.Tables, table => table.Name == "unknown_Ø" && table.Rows.RowCount == 1);
        Assert.Contains(inventory.Tables, table => table.Kind == "shadow" && table.Rows.RowCount != 0);
        Assert.Contains(inventory.Tables, table => table.Name == "fixture_fts" && table.Rows.RowCount == 1);
        Assert.Contains(inventory.Tables, table => table.Name == "composite_key" && table.WithoutRowId && table.RowIdExpression is null);
        Assert.Contains(inventory.Tables.Single(table => table.Name == "generated_values").Columns, column => column.Hidden == 3);
        Assert.Equal(CaptureFixture.Hash(fixture.BundlePath(values.Rows.Path)), values.Rows.Sha256);
        Assert.Contains(ReadRows(fixture.BundlePath(inventory.Schema.Path)).SelectMany(row => row.Values),
            value => value.Type == 3 && Encoding.UTF8.GetString(value.Bytes) == "fixture_expression");
        Assert.Contains(ReadRows(fixture.BundlePath(inventory.Schema.Path)).SelectMany(row => row.Values),
            value => value.Type == 3 && Encoding.UTF8.GetString(value.Bytes) == "fixture_view");
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("change")]
    [InlineData("hydration")]
    [InlineData("topic")]
    [InlineData("run-item")]
    [InlineData("accepted-receipt")]
    [InlineData("partition")]
    [InlineData("run-count")]
    [InlineData("receipt-revision")]
    [InlineData("receipt-attempt")]
    [InlineData("lease")]
    [InlineData("mode-kind")]
    [InlineData("run-kind")]
    [InlineData("receipt-kind")]
    [InlineData("snapshot-missing")]
    [InlineData("snapshot-outside")]
    [InlineData("snapshot-checksum")]
    [InlineData("snapshot-provenance")]
    [InlineData("snapshot-uppercase")]
    [InlineData("descriptor")]
    [InlineData("foreign-key")]
    public async Task Inventory_RequiresRetainedReferenceClosureAndPlannerKind(string defect)
    {
        using CaptureFixture fixture = new(currentSchema: true);
        fixture.SeedCompleteGraph();
        if (defect.StartsWith("snapshot-", StringComparison.Ordinal) || defect == "descriptor")
        {
            await fixture.SeedSnapshotAsync();
        }
        using (SqliteConnection connection = CaptureFixture.Open(fixture.Database))
        {
            string sql = defect switch
            {
                "repository" => "UPDATE planned_ticket_repo_changes SET TicketRepoId = 'missing'",
                "change" => "UPDATE planned_ticket_repo_impacts SET TicketRepoChangeId = 'missing'",
                "hydration" => "UPDATE planned_jira_hydration SET IssueKey = 'MISSING-1'",
                "topic" => "UPDATE planned_ticket_topic_members SET TopicRowId = 987",
                "run-item" => "UPDATE authoring_run_items SET RunId = 'missing'",
                "accepted-receipt" => "UPDATE authoring_run_items SET AcceptedReceiptId = 'missing'",
                "partition" => "UPDATE planned_ticket_partition_receipts SET InputFingerprint = 'other'",
                "run-count" => "UPDATE authoring_runs SET TotalItems = 3",
                "receipt-revision" => "UPDATE authoring_result_receipts SET ObservedSourceRevision = 'other'",
                "receipt-attempt" => "UPDATE authoring_run_attempts SET Status = 'error'",
                "lease" => "UPDATE authoring_run_items SET PostPersistenceLeaseId = 'retained-lease'",
                "mode-kind" => "UPDATE authoring_processor_modes SET ProcessorKind = 'jira-preparer'",
                "run-kind" => "UPDATE authoring_runs SET ProcessorKind = 'jira-ballotnotes'",
                "receipt-kind" => "ALTER TABLE authoring_result_receipts ADD COLUMN ProcessorKind TEXT; UPDATE authoring_result_receipts SET ProcessorKind = 'other'",
                "snapshot-checksum" => "UPDATE authoring_review_snapshots SET ChecksumSha256 = '" + new string('0', 64) + "'",
                "snapshot-provenance" => "UPDATE authoring_review_snapshots SET AuthoringEpoch = 99",
                "snapshot-uppercase" => """
                    ALTER TABLE authoring_review_snapshots RENAME TO intermediate_snapshots;
                    ALTER TABLE intermediate_snapshots RENAME TO AUTHORING_REVIEW_SNAPSHOTS;
                    """,
                "foreign-key" => """
                    PRAGMA foreign_keys = OFF;
                    CREATE TABLE fk_parent(Id INTEGER PRIMARY KEY);
                    CREATE TABLE fk_child(Id INTEGER PRIMARY KEY, ParentId INTEGER REFERENCES fk_parent(Id));
                    INSERT INTO fk_child VALUES(1, 99);
                    """,
                _ => "SELECT 1",
            };
            CaptureFixture.Execute(connection, sql);
            if (defect == "snapshot-outside")
            {
                CaptureFixture.Execute(connection, "UPDATE authoring_review_snapshots SET Path = @path",
                    ("@path", Path.Combine(fixture.Root, "not-declared", "snapshot.db")));
            }
        }
        if (defect == "snapshot-missing")
        {
            File.Delete(Path.Combine(fixture.Settings.Snapshots, "snapshot.db"));
        }
        if (defect == "snapshot-uppercase")
        {
            File.Delete(Path.Combine(fixture.Settings.Snapshots, "snapshot.db"));
            File.Delete(Path.Combine(fixture.Settings.Snapshots, "snapshot.descriptor.json"));
        }
        if (defect == "descriptor")
        {
            string descriptorPath = Path.Combine(fixture.Settings.Snapshots, "snapshot.descriptor.json");
            AuthoringSnapshotDescriptor descriptor = JsonSerializer.Deserialize<AuthoringSnapshotDescriptor>(
                File.ReadAllText(descriptorPath), PlannerRetainedStateEvidence.Json)!;
            File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor with { Sequence = 99 }, PlannerRetainedStateEvidence.Json));
        }
        Dictionary<string, string> before = fixture.OriginalHashes();
        PlannerRetainedStateException error = await Assert.ThrowsAsync<PlannerRetainedStateException>(
            () => new PlannerRetainedStateCapture().CaptureAsync(fixture.Settings));
        Assert.Equal(20, error.ExitCode);
        string expectedCategory = defect switch
        {
            "mode-kind" or "run-kind" or "receipt-kind" => "wrong-processor-kind",
            "snapshot-outside" => "unmapped-retained-path",
            "snapshot-missing" => "descriptor-artifact-missing",
            "snapshot-uppercase" => "required-snapshot-artifact-missing",
            "snapshot-checksum" or "snapshot-provenance" or "descriptor" => "snapshot-checksum-count-or-provenance-mismatch",
            _ => "broken-required-relationship",
        };
        Assert.Equal(expectedCategory, error.Category);
        fixture.AssertOriginals(before);
        Assert.True(File.Exists(fixture.BundlePath("raw.catalog.json")));
        Assert.True(File.Exists(fixture.BundlePath("capture.incomplete.json")));
        Assert.False(File.Exists(fixture.BundlePath("capture.complete.json")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("jira-fhir")]
    public async Task Inventory_AdmitsEmptyLegacyKindWithoutInventingMissingAuthoringTables(string? kind)
    {
        using CaptureFixture fixture = new();
        using (SqliteConnection connection = CaptureFixture.Open(fixture.Database))
        {
            CaptureFixture.Execute(connection, """
                CREATE TABLE authoring_processor_modes(
                    ProcessorKind TEXT, Mode TEXT, Epoch INTEGER, RevalidationRequired INTEGER,
                    RevalidationRunId TEXT, UpdatedAt TEXT);
                INSERT INTO authoring_processor_modes VALUES(@kind, 'legacy', 0, 0, NULL, @at);
                """, ("@kind", kind is null ? DBNull.Value : kind), ("@at", CaptureFixture.At));
        }
        PlannerRetainedStateManifest manifest = await new PlannerRetainedStateCapture().CaptureAsync(fixture.Settings);
        Assert.Contains("authoring_runs", manifest.Databases[0].AbsentLegacyTables);
        Assert.Equal(1, manifest.Databases[0].Tables.Single(table => table.Name == "authoring_processor_modes").Rows.RowCount);
        Assert.All(manifest.Databases[0].Checks, check => Assert.NotEqual("failed", check.Status));
    }

    [Fact]
    public async Task Inventory_RefusesUnreadableVirtualTableWithoutSkippingItsEvidence()
    {
        using CaptureFixture fixture = new();
        using (SqliteConnection connection = CaptureFixture.Open(fixture.Database))
        {
            CaptureFixture.Execute(connection, """
                CREATE VIRTUAL TABLE unreadable USING fts5(body);
                INSERT INTO unreadable VALUES('retained');
                PRAGMA writable_schema = ON;
                UPDATE sqlite_schema SET sql = replace(sql, 'fts5', 'unavailable_module') WHERE name = 'unreadable';
                PRAGMA writable_schema = OFF;
                """);
        }
        Dictionary<string, string> before = fixture.OriginalHashes();
        PlannerRetainedStateException error = await Assert.ThrowsAsync<PlannerRetainedStateException>(
            () => new PlannerRetainedStateCapture().CaptureAsync(fixture.Settings));
        Assert.Equal("unsupported-table-reader", error.Category);
        Assert.True(File.Exists(fixture.BundlePath("raw.catalog.json")));
        Assert.False(File.Exists(fixture.BundlePath("capture.complete.json")));
        fixture.AssertOriginals(before);
    }

    [Theory]
    [InlineData("inside-original")]
    [InlineData("contains-original")]
    [InlineData("case-overlap")]
    [InlineData("existing-destination")]
    [InlineData("same-backup")]
    [InlineData("hardlink-database")]
    [InlineData("hardlink-member")]
    [InlineData("junction-member")]
    [InlineData("junction-ancestor")]
    [InlineData("owner-lock-hardlink")]
    public async Task Capture_RefusesAliasesOverlapAndExistingDestination(string defect)
    {
        using CaptureFixture fixture = new();
        string? junction = null;
        switch (defect)
        {
            case "inside-original": fixture.Settings = fixture.Settings with { Bundle = Path.Combine(fixture.Original, "capture") }; break;
            case "contains-original": fixture.Settings = fixture.Settings with { Bundle = fixture.Root }; break;
            case "case-overlap": fixture.Settings = fixture.Settings with { Bundle = Path.Combine(fixture.Original.ToUpperInvariant(), "capture") }; break;
            case "existing-destination": Directory.CreateDirectory(fixture.Settings.Bundle); File.WriteAllText(fixture.BundlePath("keep"), "retained"); break;
            case "same-backup": fixture.Settings = fixture.Settings with { PreCutoverBackup = fixture.Database }; break;
            case "hardlink-database": HardLink(Path.Combine(fixture.Root, "alias.db"), fixture.Database); break;
            case "hardlink-member":
                File.WriteAllText(Path.Combine(fixture.Original, "opaque"), "retained");
                HardLink(Path.Combine(fixture.Root, "alias"), Path.Combine(fixture.Original, "opaque"));
                break;
            case "owner-lock-hardlink":
                File.WriteAllText(fixture.Database + ".owner.lock", "coordination");
                HardLink(Path.Combine(fixture.Root, "alias-lock"), fixture.Database + ".owner.lock");
                break;
            case "junction-member":
            case "junction-ancestor":
                string target = Path.Combine(fixture.Root, "target");
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "untouched"), "retained");
                junction = defect == "junction-member" ? Path.Combine(fixture.Original, "junction") : Path.Combine(fixture.Root, "junction");
                CreateJunction(junction, defect == "junction-member" ? target : fixture.Original);
                if (defect == "junction-ancestor")
                {
                    fixture.Settings = fixture.Settings with { Database = Path.Combine(junction, "planner.db") };
                }
                break;
        }
        try
        {
            PlannerRetainedStateException error = await Assert.ThrowsAsync<PlannerRetainedStateException>(
                () => new PlannerRetainedStateCapture().CaptureAsync(fixture.Settings));
            Assert.Equal(20, error.ExitCode);
            Assert.False(File.Exists(fixture.Database + ".owner.lock") && defect != "owner-lock-hardlink");
            Assert.False(File.Exists(fixture.BundlePath("capture.complete.json")));
            if (defect == "existing-destination")
            {
                Assert.Equal("retained", File.ReadAllText(fixture.BundlePath("keep")));
            }
        }
        finally
        {
            if (junction is not null)
            {
                Directory.Delete(junction);
            }
        }
    }

    [Theory]
    [InlineData(@"relative\planner.db")]
    [InlineData(@"C:planner.db")]
    [InlineData(@"\\server\share\planner.db")]
    [InlineData(@"\\?\C:\planner.db")]
    [InlineData(@"\\.\C:\planner.db")]
    [InlineData(@"C:\x\..\planner.db")]
    [InlineData(@"C:\x\.\planner.db")]
    [InlineData(@"C:\x\planner.db:stream")]
    [InlineData(@"C:\x\planner.db.")]
    [InlineData(@"C:\x\NUL.db")]
    public async Task Capture_RefusesNonlocalOrAmbiguousPathsWithoutDatabaseAccess(string database)
    {
        using CaptureFixture fixture = new();
        PlannerRetainedStateException error = await Assert.ThrowsAsync<PlannerRetainedStateException>(() =>
            new PlannerRetainedStateCapture().CaptureAsync(fixture.Settings with { Database = database }));
        Assert.Equal(20, error.ExitCode);
        Assert.False(File.Exists(fixture.Database + ".owner.lock"));
        Assert.False(Directory.Exists(fixture.Settings.Bundle));
    }

    [Theory]
    [InlineData("raw-and-rehearsal")]
    [InlineData("coordinator-temp")]
    [InlineData("snapshot-temp")]
    [InlineData("utf16")]
    [InlineData("evidence-root")]
    public async Task Capture_RefusesExpandedPathBudgetBeforeSqliteWork(string expansion)
    {
        using CaptureFixture fixture = new();
        if (expansion is "raw-and-rehearsal" or "utf16")
        {
            string name = expansion == "utf16" ? string.Concat(Enumerable.Repeat("🦉", 86)) : new string('d', 172);
            string database = Path.Combine(fixture.Original, name + ".db");
            File.Move(fixture.Database, database);
            fixture.Settings = fixture.Settings with { Database = database };
        }
        else if (expansion == "coordinator-temp")
        {
            fixture.Settings = fixture.Settings with { PreCutoverBackup = Path.Combine(fixture.Original, new string('b', 149) + ".db") };
        }
        else if (expansion == "snapshot-temp")
        {
            fixture.Settings = fixture.Settings with { Snapshots = Path.Combine(fixture.Original, new string('s', 154)) };
        }
        else
        {
            string parent = Path.Combine(fixture.Root, new string('e', 30));
            Directory.CreateDirectory(parent);
            fixture.Settings = fixture.Settings with { Bundle = Path.Combine(parent, "capture") };
        }
        bool sqliteReached = false;
        PlannerRetainedStateException error = await Assert.ThrowsAsync<PlannerRetainedStateException>(() =>
            new PlannerRetainedStateCapture(stage => sqliteReached |= stage == "before-sqlite").CaptureAsync(fixture.Settings));
        Assert.Equal("path-budget-exceeded", error.Category);
        Assert.False(sqliteReached);
        Assert.False(File.Exists(fixture.Settings.Database + ".owner.lock"));
    }

    [Theory]
    [InlineData("raw-copied")]
    [InlineData("inventories-written")]
    [InlineData("before-completion")]
    [InlineData("marker-written")]
    public async Task Capture_FailureCannotProduceCompletionMarker(string failAt)
    {
        using CaptureFixture fixture = new();
        Dictionary<string, string> before = fixture.OriginalHashes();
        await Assert.ThrowsAsync<IOException>(() => new PlannerRetainedStateCapture(stage =>
        {
            if (stage == failAt)
            {
                throw new IOException("injected evidence write failure");
            }
        }).CaptureAsync(fixture.Settings));
        fixture.AssertOriginals(before);
        Assert.False(File.Exists(fixture.BundlePath("capture.complete.json")));
        Assert.True(File.Exists(fixture.BundlePath("capture.incomplete.json")));
        Assert.True(File.Exists(fixture.BundlePath("raw.catalog.json")));
    }

    [Fact]
    public async Task Capture_CancellationKeepsIncompleteRawEvidenceAndReleasesOwnership()
    {
        using CaptureFixture fixture = new();
        using CancellationTokenSource cancellation = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PlannerRetainedStateCapture(stage =>
        {
            if (stage == "raw-copied")
            {
                cancellation.Cancel();
            }
        }).CaptureAsync(fixture.Settings, cancellation.Token));
        Assert.True(File.Exists(fixture.BundlePath("capture.incomplete.json")));
        Assert.False(File.Exists(fixture.BundlePath("capture.complete.json")));
        using PlannerDatabase owner = new(fixture.Database, NullLogger<PlannerDatabase>.Instance, readOnly: true);
        owner.AcquireStartupOwnership();
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("inventory")]
    [InlineData("raw")]
    [InlineData("safety")]
    [InlineData("marker")]
    [InlineData("candidate")]
    [InlineData("binary-catalog")]
    [InlineData("extra-file")]
    [InlineData("empty-directory")]
    [InlineData("removed-empty-directory")]
    public async Task Consumer_RejectsMarkerCatalogOrCandidateMismatch(string defect)
    {
        using CaptureFixture fixture = new();
        Directory.CreateDirectory(Path.Combine(fixture.Original, "empty"));
        PlannerRetainedStateManifest manifest = await new PlannerRetainedStateCapture().CaptureAsync(fixture.Settings);
        string? target = defect switch
        {
            "manifest" => "manifest.json",
            "inventory" => manifest.Databases[0].Tables[0].Rows.Path,
            "raw" => manifest.Artifacts[0].RawPath,
            "safety" => manifest.Databases[0].SafetyPath,
            "marker" => "capture.complete.json",
            _ => null,
        };
        if (target is not null)
        {
            using FileStream change = new(fixture.BundlePath(target), FileMode.Open, FileAccess.Write, FileShare.None);
            change.Position = 0;
            change.WriteByte(0xff);
        }
        if (defect == "extra-file")
        {
            File.WriteAllText(fixture.BundlePath("unlisted.txt"), "not attested");
        }
        if (defect == "empty-directory")
        {
            Directory.CreateDirectory(fixture.BundlePath("unlisted-directory"));
        }
        if (defect == "removed-empty-directory")
        {
            PlannerRetainedDirectory directory = manifest.Directories.Single(directory => directory.RelativePath == "empty");
            Directory.Delete(fixture.BundlePath("raw/" + directory.RootId + "/" + directory.RelativePath));
        }
        if (defect == "binary-catalog")
        {
            string markerPath = fixture.BundlePath("capture.complete.json");
            PlannerRetainedStateCompletion completion = PlannerRetainedStateEvidence.ReadJson<PlannerRetainedStateCompletion>(markerPath);
            File.WriteAllText(markerPath, JsonSerializer.Serialize(completion with
            {
                Binaries = completion.Binaries.Select((binary, index) =>
                    index == 0 ? binary with { Sha256 = new string('0', 64) } : binary).ToArray(),
            }, PlannerRetainedStateEvidence.Json));
        }
        PlannerRetainedStateException error = await Assert.ThrowsAsync<PlannerRetainedStateException>(async () =>
        {
            using PlannerRetainedVerifiedCapture capture = await PlannerRetainedStateCapture.OpenVerifiedAsync(
                fixture.Settings.Bundle, defect == "candidate" ? new string('c', 40) : CaptureFixture.Commit, CaptureFixture.Tree);
        });
        Assert.Equal(defect switch
        {
            "candidate" => "candidate-or-format-mismatch",
            "binary-catalog" => "manifest-or-binary-mismatch",
            "marker" => "invalid-completion-metadata",
            "extra-file" or "empty-directory" or "removed-empty-directory" => "bundle-membership-mismatch",
            _ => "bundle-hash-mismatch",
        }, error.Category);
    }

    [Fact]
    public async Task Consumer_ValidatesOpaqueOriginalMapWithoutAccessingOriginalPaths()
    {
        using CaptureFixture fixture = new();
        await new PlannerRetainedStateCapture().CaptureAsync(fixture.Settings);
        string unavailable = Path.Combine(fixture.Root, "unavailable");
        Directory.Move(fixture.Original, unavailable);
        using (PlannerRetainedVerifiedCapture verified = await fixture.OpenVerifiedAsync())
        {
            Assert.False(Directory.Exists(fixture.Original));
            Assert.NotEmpty(verified.Manifest.Databases[0].Tables);
        }
        Directory.Move(unavailable, fixture.Original);
        string manifestPath = fixture.BundlePath("manifest.json");
        PlannerRetainedStateManifest manifest = PlannerRetainedStateEvidence.ReadJson<PlannerRetainedStateManifest>(manifestPath);
        manifest = manifest with { SettingsPaths = manifest.SettingsPaths with
        {
            Database = manifest.SettingsPaths.Database with { RelativePath = "../outside.db" },
        }};
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, PlannerRetainedStateEvidence.Json));
        string markerPath = fixture.BundlePath("capture.complete.json");
        PlannerRetainedStateCompletion marker = PlannerRetainedStateEvidence.ReadJson<PlannerRetainedStateCompletion>(markerPath);
        string hash = CaptureFixture.Hash(manifestPath);
        marker = marker with
        {
            ManifestSha256 = hash,
            Files = marker.Files.Select(file => file.Path == "manifest.json"
                ? file with { Sha256 = hash, Length = new FileInfo(manifestPath).Length } : file).ToArray(),
        };
        File.WriteAllText(markerPath, JsonSerializer.Serialize(marker, PlannerRetainedStateEvidence.Json));
        PlannerRetainedStateException error = await Assert.ThrowsAsync<PlannerRetainedStateException>(async () =>
        {
            using PlannerRetainedVerifiedCapture verified = await fixture.OpenVerifiedAsync();
        });
        Assert.Equal("invalid-root-map", error.Category);
    }

    internal sealed class CaptureFixture : IDisposable
    {
        internal const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        internal const string Tree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        internal static readonly string At = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).ToString("O");
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "prs-" + Guid.NewGuid().ToString("N")[..8]);
        internal string Original => Path.Combine(Root, "o");
        internal string Database => Settings.Database;
        internal PlannerRetainedStateSettings Settings { get; set; }

        internal CaptureFixture(bool createDatabase = true, bool currentSchema = false)
        {
            Directory.CreateDirectory(Original);
            Settings = new(Path.Combine(Original, "planner.db"), "", Path.Combine(Original, "snapshots"),
                false, 1, true, true, true, Path.Combine(Root, "capture"), Commit, Tree, []);
            Assert.True(Root.Length <= 60, "Fixture needs a short unique local temp root.");
            if (createDatabase)
            {
                if (currentSchema)
                {
                    using PlannerDatabase database = new(Database, NullLogger<PlannerDatabase>.Instance);
                    database.Initialize();
                    using SqliteConnection connection = Open(Database);
                    InsertSource(connection);
                }
                else
                {
                    CreateLegacy(Database);
                }
            }
        }

        internal string BundlePath(string relative) => Path.Combine(Settings.Bundle, relative.Replace('/', Path.DirectorySeparatorChar));

        internal string[] Arguments() =>
        [
            "retained-state", "capture", "--database", Settings.Database,
            "--pre-cutover-backup", Settings.PreCutoverBackup, "--snapshots", Settings.Snapshots,
            "--activate-run-backed", Settings.ActivateRunBackedAuthoring ? "true" : "false",
            "--snapshot-schema-version", Settings.SnapshotSchemaVersion.ToString(CultureInfo.InvariantCulture),
            "--start-processing-on-startup", Settings.StartProcessingOnStartup ? "true" : "false",
            "--reconcile-snapshots-on-startup", Settings.ReconcileSnapshotsOnStartup ? "true" : "false",
            "--hydration-backfill-on-startup", Settings.HydrationBackfillOnStartup ? "true" : "false",
            "--bundle", Settings.Bundle, "--candidate-commit", Settings.CandidateCommit,
            "--candidate-tree", Settings.CandidateTree, "--confirm-owner-settings",
        ];

        internal Task<PlannerRetainedVerifiedCapture> OpenVerifiedAsync() =>
            PlannerRetainedStateCapture.OpenVerifiedAsync(Settings.Bundle, Commit, Tree);

        internal Dictionary<string, string> OriginalHashes() => Directory.EnumerateFiles(Original, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, Hash, StringComparer.Ordinal);

        internal void AssertOriginals(Dictionary<string, string> before)
        {
            foreach ((string path, string hash) in before)
            {
                Assert.True(File.Exists(path), "A synthetic original disappeared.");
                Assert.Equal(hash, Hash(path));
            }
            Assert.Equal(before.Keys.Where(path => path != Database + ".owner.lock").Order(StringComparer.Ordinal),
                Directory.GetFiles(Original, "*", SearchOption.AllDirectories)
                    .Where(path => path != Database + ".owner.lock").Order(StringComparer.Ordinal));
        }

        internal static string Hash(string path)
        {
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        }

        internal static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
        {
            SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = mode, Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        internal string ScalarString(string path, string sql)
        {
            using SqliteConnection connection = SqliteReviewSnapshotValidator.OpenReadOnlyAsync(path).GetAwaiter().GetResult();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
        }

        internal static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach ((string name, object value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
            command.ExecuteNonQuery();
        }

        private static void CreateLegacy(string path)
        {
            using SqliteConnection connection = Open(path);
            Execute(connection, """
                CREATE TABLE jira_processing_source_tickets(
                    RowId INTEGER UNIQUE PRIMARY KEY NOT NULL, Id TEXT UNIQUE NOT NULL,
                    Key TEXT NOT NULL, Title TEXT NOT NULL, Description TEXT, Project TEXT NOT NULL,
                    Status TEXT NOT NULL, WorkGroup TEXT NOT NULL, Type TEXT NOT NULL,
                    SourceTicketShape TEXT NOT NULL, LastSyncedAt TEXT NOT NULL, LastUpdated TEXT,
                    StartedProcessingAt TEXT, CompletedProcessingAt TEXT, LastProcessingAttemptAt TEXT,
                    ProcessingStatus TEXT NOT NULL, ProcessingError TEXT, ProcessingAttemptCount INTEGER NOT NULL,
                    ErrorMessage TEXT, AgentExitCode INTEGER, ErrorOccurredAt TEXT,
                    Specification TEXT NOT NULL, SourceProjectLastSuccessfulRefreshAt TEXT, SourceContentRevision INTEGER);
                """);
            InsertSource(connection);
        }

        private static void InsertSource(SqliteConnection connection) => Execute(connection, """
            INSERT INTO jira_processing_source_tickets(
                RowId, Id, Key, Title, Description, Project, Status, WorkGroup, Type,
                SourceTicketShape, LastSyncedAt, LastUpdated, ProcessingStatus, ProcessingAttemptCount,
                Specification, SourceProjectLastSuccessfulRefreshAt, SourceContentRevision)
            VALUES(27, 'legacy-id', 'FHIR-1', 'retained title', @description, 'FHIR',
                'Resolved - change required', 'FHIR Infrastructure', 'Change Request',
                'fhir', @at, NULL, 'complete', 3, 'FHIR', @at, 731);
            """, ("@at", At), ("@description", "retained description\0\n|:"));

        internal void SeedCommittedWal()
        {
            string donor = Path.Combine(Root, "donor.db");
            CreateLegacy(donor);
            using SqliteConnection writer = Open(donor);
            Execute(writer, """
                PRAGMA journal_mode = WAL;
                PRAGMA wal_autocheckpoint = 0;
                CREATE TABLE wal_only(Id INTEGER PRIMARY KEY, Value TEXT);
                INSERT INTO wal_only VALUES(1, 'committed WAL value');
                """);
            // Fixture construction only: this synchronous donor has one controlled,
            // idle writer. Preserve its pre-close family in independently created
            // synthetic input files; closing the donor may checkpoint only the donor.
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                using FileStream source = new(donor + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using FileStream destination = new(Database + suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(destination);
            }
            // A captured WAL-index image is not an authority for recovery.
            // Poison the disposable fixture SHM to prove capture regenerates it.
            using FileStream shm = new(Database + "-shm", FileMode.Open, FileAccess.Write, FileShare.None);
            shm.Write(new byte[64]);
        }

        internal void SeedDistinctBackup()
        {
            string backup = Path.Combine(Original, "pre-cutover.db");
            CreateLegacy(backup);
            using (SqliteConnection connection = Open(backup))
            {
                Execute(connection, "UPDATE jira_processing_source_tickets SET Title = 'distinct pre-cutover bytes'");
            }
            Settings = Settings with { PreCutoverBackup = backup, ActivateRunBackedAuthoring = true };
        }

        internal void SeedCompleteGraph()
        {
            using SqliteConnection connection = Open(Database);
            Execute(connection, """
                INSERT INTO planned_tickets(Id, Key, Resolution, ResolutionSummary, FeatureProposal, DesignRationale, SavedAt)
                    VALUES('plan-1','FHIR-1','Persuasive','retained summary','proposal','rationale',@at);
                INSERT INTO planned_ticket_repos(Id, IssueKey, RepoKey, RepoRevision, Justification)
                    VALUES('repo-1','FHIR-1','HL7/fhir','revision','justification');
                INSERT INTO planned_ticket_repo_changes(Id, IssueKey, TicketRepoId, RepoKey, ChangeSequence,
                    FilePath, ChangeTitle, ChangeDescription, ReplacementLines, Reason)
                    VALUES('change-1','FHIR-1','repo-1','HL7/fhir',1,'source/file.html','title','description','["a","","b"]','reason');
                INSERT INTO planned_ticket_repo_impacts(Id, IssueKey, TicketRepoId, RepoKey, TicketRepoChangeId, AffectedFilePath, HowAffected)
                    VALUES('impact-1','FHIR-1','repo-1','HL7/fhir','change-1','source/other.html','impact');
                INSERT INTO planned_ticket_change_validations(Id, IssueKey, TicketRepoId, RepoKey, ValidationSequence, Action)
                    VALUES('validation-1','FHIR-1','repo-1','HL7/fhir',1,'validate');
                INSERT INTO planned_ticket_testing_considerations(Id, IssueKey, TicketRepoId, RepoKey, ConsiderationSequence, Consideration)
                    VALUES('testing-1','FHIR-1','repo-1','HL7/fhir',1,'test');
                INSERT INTO planned_ticket_open_questions(Id, IssueKey, TicketRepoId, RepoKey, QuestionSequence, Question)
                    VALUES('question-1','FHIR-1','repo-1','HL7/fhir',1,'question');
                INSERT INTO planned_ticket_jira_content(TicketKey, DescriptionHtml, ResolutionDescriptionHtml)
                    VALUES('FHIR-1','<p>description</p>','<p>resolution</p>');
                INSERT INTO planned_ticket_hydration(IssueKey, HydratedAt, HydrationStatus, DescriptionPlain)
                    VALUES('FHIR-1',@at,'resolved','description');
                INSERT INTO planned_jira_hydration(IssueKey, JiraKey, HydratedAt, HydrationStatus, Title, WorkGroupClean, Specification, Type)
                    VALUES('FHIR-1','FHIR-1',@at,'resolved','self','FHIRInfrastructure','FHIR','Change Request'),
                          ('FHIR-1','FHIR-9',@at,'resolved','linked','FHIRInfrastructure','FHIR','Change Request');
                INSERT INTO planned_zulip_hydration(IssueKey, ZulipThreadId, HydratedAt, HydrationStatus) VALUES('FHIR-1','thread',@at,'resolved');
                INSERT INTO planned_github_hydration(IssueKey, GitHubItemId, HydratedAt, HydrationStatus) VALUES('FHIR-1','HL7/fhir#1',@at,'resolved');
                INSERT INTO planned_repo_hydration(IssueKey, RepoKey, HydratedAt, HydrationStatus) VALUES('FHIR-1','HL7/fhir',@at,'resolved');
                INSERT INTO planned_ticket_related_jira(IssueKey,JiraKey,Source) VALUES('FHIR-1','FHIR-9','Linked');
                INSERT INTO planned_ticket_related_zulip(IssueKey,ZulipThreadId) VALUES('FHIR-1','thread');
                INSERT INTO planned_ticket_related_github(IssueKey,GitHubItemId) VALUES('FHIR-1','HL7/fhir#1');
                INSERT INTO planned_ticket_jira_xref(IssueKey,JiraKey,Source) VALUES('FHIR-1','FHIR-9','Linked');
                INSERT INTO planned_ticket_topics(RowId,Id,WorkGroupClean,WorkGroupDisplay,Specification,Type,ShortDescription,LongerDescription,SavedAt)
                    VALUES(1,'topic-1','FHIRInfrastructure','FHIR Infrastructure','FHIR','Change Request','short','long',@at);
                INSERT INTO planned_ticket_topic_groups(RowId,Id,TopicRowId,FirstTicketKey,Rationale,OrderInTopic,SavedAt)
                    VALUES(1,'group-1',1,'FHIR-1','rationale',1,@at);
                INSERT INTO planned_ticket_topic_members(Id,TopicRowId,TopicGroupRowId,TicketKey,OrderInContainer)
                    VALUES('member-1',1,1,'FHIR-1',1);
                INSERT INTO planned_ticket_topic_repos(Id,TopicRowId,RepoKey,OrderInTopic) VALUES('topic-repo-1',1,'HL7/fhir',1);
                INSERT INTO jira_review_workgroups(Code,Name,NameClean,UpdatedAt) VALUES('fhir','FHIR Infrastructure','FHIRInfrastructure',@at);
                INSERT INTO authoring_processor_modes(ProcessorKind,Mode,Epoch,RevalidationRequired,UpdatedAt)
                    VALUES('jira-fhir','run-backed',1,0,@at);
                INSERT INTO authoring_runs(Id,ProcessorKind,AuthoringEpoch,Status,Purpose,DatabaseOnly,TotalItems,CreatedAt)
                    VALUES('run-1','jira-fhir',1,'completed','authoring',0,1,@at);
                INSERT INTO authoring_run_items(Id,RunId,BusinessKey,ItemKind,ExpectedSourceRevision,Status,
                    CurrentOperationId,AcceptedReceiptId,AttemptCount,CreatedAt)
                    VALUES('item-1','run-1','FHIR-1','fhir','revision','complete','operation-1','receipt-1',1,@at);
                INSERT INTO authoring_run_attempts(OperationId,RunId,RunItemId,AttemptNumber,TokenVerifier,Status,ContentHash,ObservedSourceRevision,CreatedAt)
                    VALUES('operation-1','run-1','item-1',1,'synthetic-verifier','accepted','hash','revision',@at);
                INSERT INTO authoring_result_receipts(Id,OperationId,RunId,RunItemId,BusinessKey,ContentHash,ExpectedSourceRevision,
                    ObservedSourceRevision,AuthoringEpoch,PersistedAt)
                    VALUES('receipt-1','operation-1','run-1','item-1','FHIR-1','hash','revision','revision',1,@at);
                INSERT INTO authoring_run_input_provenance(RunId,Source,LatestSuccessfulRefreshAt,ContentRevision,CapturedAt)
                    VALUES('run-1','jira',@at,731,@at);
                INSERT INTO planned_ticket_authoring_state(TicketKey,Classification,GraphHash,ReceiptContentHash,RunId,RunItemId,OperationId,UpdatedAt)
                    VALUES('FHIR-1','receipt-backed','graph','hash','run-1','item-1','operation-1',@at);
                INSERT INTO authoring_run_stages(Id,RunId,StageName,PartitionKey,InputFingerprint,Status,AttemptCount,CreatedAt)
                    VALUES('stage-1','run-1','grouping','partition','fingerprint','complete',1,@at);
                INSERT INTO planned_ticket_partition_receipts(RunId,StageId,PartitionKey,InputFingerprint,TopicRows,TopicGroupRows,MemberRows,PersistedAt)
                    VALUES('run-1','stage-1','partition','fingerprint',1,1,1,@at);
                INSERT INTO planned_ticket_run_item_partitions(RunItemId,TicketKey,WorkGroupClean,Specification,Type,CapturedAt)
                    VALUES('item-1','FHIR-1','FHIRInfrastructure','FHIR','Change Request',@at);
                INSERT INTO authoring_mutation_fences(ProcessorKind,RunId,LeaseId,AcquiredAt)
                    VALUES('jira-fhir','maintenance:old-generation:fixture','retained-lease',@at);
                UPDATE jira_processing_source_tickets SET CompletionId='receipt-1';
                """, ("@at", At));
        }

        internal async Task SeedSnapshotAsync()
        {
            Directory.CreateDirectory(Settings.Snapshots);
            string snapshot = Path.Combine(Settings.Snapshots, "snapshot.db");
            Dictionary<string, long> counts = [];
            using (SqliteConnection source = Open(Database))
            using (SqliteConnection destination = Open(snapshot))
            {
                source.BackupDatabase(destination);
                foreach (string table in PlannedTicketSnapshotSchemaV1.CountedTables)
                {
                    using SqliteCommand count = destination.CreateCommand();
                    count.CommandText = "SELECT COUNT(*) FROM " + PlannerRetainedStateInventory.Quote(table);
                    counts.Add(table, Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture));
                }
                Execute(destination, """
                    CREATE TABLE authoring_snapshot_provenance(
                        SnapshotId TEXT NOT NULL,ProcessorKind TEXT NOT NULL,RunId TEXT NOT NULL,
                        AuthoringEpoch INTEGER NOT NULL,Sequence INTEGER NOT NULL,SchemaVersion INTEGER NOT NULL,
                        ItemCount INTEGER NOT NULL,ReceiptCount INTEGER NOT NULL,TableCountsJson TEXT NOT NULL,CreatedAt TEXT NOT NULL);
                    INSERT INTO authoring_snapshot_provenance VALUES('snapshot-1','jira-fhir','run-1',1,1,1,1,1,@counts,@at);
                    """, ("@counts", JsonSerializer.Serialize(counts)), ("@at", At));
                await new AuthoringSnapshotSanitizer(PlannedTicketSnapshotSchemaV1.Catalog).SanitizeAsync(destination);
            }
            string hash = Hash(snapshot);
            long size = new FileInfo(snapshot).Length;
            using (SqliteConnection connection = Open(Database))
            {
                Execute(connection, """
                    INSERT INTO authoring_review_snapshots(Id,ProcessorKind,RunId,AuthoringEpoch,Sequence,SchemaVersion,
                        Status,TempPath,Path,ChecksumSha256,SizeBytes,ItemCount,ReceiptCount,TableCountsJson,CreatedAt)
                    VALUES('snapshot-1','jira-fhir','run-1',1,1,1,'ready',@temp,@path,@hash,@size,1,1,@counts,@at);
                    UPDATE authoring_runs SET SnapshotId='snapshot-1' WHERE Id='run-1';
                    """, ("@temp", snapshot + ".tmp"), ("@path", snapshot), ("@hash", hash), ("@size", size),
                    ("@counts", JsonSerializer.Serialize(counts)), ("@at", At));
            }
            AuthoringSnapshotDescriptor descriptor = new("jira-fhir", "run-1", "snapshot-1", 1, 1, 1,
                hash, size, 1, 1, counts, "snapshot.db", DateTimeOffset.Parse(At, CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(Settings.Snapshots, "snapshot.descriptor.json"),
                JsonSerializer.Serialize(descriptor, PlannerRetainedStateEvidence.Json));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed record TypedValue(int Type, byte[] Bytes);
    private sealed record TypedRow(IReadOnlyList<TypedValue> Values);

    private static List<TypedRow> ReadRows(string path)
    {
        using BinaryReader reader = new(File.OpenRead(path), Encoding.UTF8);
        Assert.Equal("FAROWS1\0"u8.ToArray(), reader.ReadBytes(8));
        int columns = reader.ReadInt32();
        for (int index = 0; index < columns; index++)
        {
            _ = reader.ReadBytes(checked((int)reader.ReadInt64()));
        }
        List<TypedRow> rows = [];
        while (reader.ReadByte() == 1)
        {
            List<TypedValue> values = [];
            for (int column = 0; column < columns; column++)
            {
                int type = reader.ReadByte();
                values.Add(new(type, reader.ReadBytes(checked((int)reader.ReadInt64()))));
            }
            rows.Add(new(values));
        }
        Assert.Equal(rows.Count, reader.ReadInt64());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        return rows;
    }

    private static void HardLink(string path, string target)
    {
        if (!CreateHardLink(path, target, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        byte[] print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        using MemoryStream buffer = new();
        using BinaryWriter writer = new(buffer, Encoding.Unicode, leaveOpen: true);
        writer.Write(0xA0000003u);
        writer.Write((ushort)(8 + substitute.Length + print.Length + 4));
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)substitute.Length);
        writer.Write((ushort)(substitute.Length + 2));
        writer.Write((ushort)print.Length);
        writer.Write(substitute);
        writer.Write((ushort)0);
        writer.Write(print);
        writer.Write((ushort)0);
        writer.Flush();
        using SafeFileHandle handle = CreateFileNative(path, 0x40000000, FileShare.ReadWrite, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        byte[] data = buffer.ToArray();
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string target, IntPtr security);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileNative(
        string name, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputLength,
        IntPtr output, int outputLength, out int returned, IntPtr overlapped);
}
