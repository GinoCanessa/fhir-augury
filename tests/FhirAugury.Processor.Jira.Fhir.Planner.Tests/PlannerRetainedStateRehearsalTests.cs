using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Common.Database;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Api;
using FhirAugury.Processor.Jira.Fhir.Planner.Maintenance;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using CaptureFixture = FhirAugury.Processor.Jira.Fhir.Planner.Tests.PlannerRetainedStateCaptureTests.CaptureFixture;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannerRetainedStateRehearsalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rehearsal_UsesFrozenRawStateAndExactExistingBackup(bool existingBackup)
    {
        using Fixture fixture = new();
        await fixture.LegacyModeAsync();
        fixture.SeedUnknown();
        if (existingBackup) fixture.Backup();
        Directory.CreateDirectory(fixture.Capture.Settings.Snapshots);
        File.WriteAllText(Path.Combine(fixture.Capture.Settings.Snapshots, "retained-note.txt"), "snapshot-directory sentinel");
        await fixture.CaptureAsync();
        Dictionary<string, string> bundle = HashTree(fixture.Bundle);
        string? backupHash = existingBackup ? CaptureFixture.Hash(fixture.BackupPath) : null;
        bool sawRaw = false;

        PlannerRetainedRehearsalResult result = await fixture.RunAsync((stage, database) =>
        {
            if (stage != "materialized") return;
            PlannerRetainedArtifact raw = fixture.Manifest!.Artifacts.Single(artifact => artifact.OriginalPath == fixture.Database);
            Assert.Equal(raw.Sha256, CaptureFixture.Hash(database));
            Assert.NotEqual(fixture.Manifest.Databases.Single(value => value.SafetyPath == "safety/planner.db").SafetySha256, raw.Sha256);
            string backup = Path.Combine(Path.GetDirectoryName(database)!, "pre.db");
            Assert.Equal(existingBackup, File.Exists(backup));
            if (existingBackup) Assert.Equal(backupHash, CaptureFixture.Hash(backup));
            Assert.False(File.Exists(database + "-shm"));
            sawRaw = true;
        });

        Passed(result);
        Assert.True(sawRaw);
        Assert.Equal(bundle, HashTree(fixture.Bundle));
        Assert.Equal(existingBackup, File.Exists(fixture.OutputPath(result.PathMap.PreCutoverBackup!)));
        Assert.Contains(result.Preservation.Artifacts, artifact => artifact.Path.EndsWith("retained-note.txt", StringComparison.Ordinal) &&
            artifact.Disposition == "byte-identical");
        Assert.All(result.Checkpoints, point => Assert.Contains(point.Deltas, delta =>
            delta.Table == "unknown_values" && delta.Status == "permitted"));
        Assert.Equal(3L, fixture.Scalar(result, "SELECT COUNT(*) FROM unknown_values"));
        using JsonDocument family = JsonDocument.Parse(File.ReadAllText(fixture.OutputPath("checkpoints/pass1-first-open/family.json")));
        Assert.True(family.RootElement.GetProperty("dbAndCapturedWalBytesUnchanged").GetBoolean());
        Assert.Equal(JsonValueKind.Null, family.RootElement.GetProperty("inputs").GetProperty("-wal").ValueKind);
        JsonElement newWal = Assert.Single(family.RootElement.GetProperty("newEmptySidecars").EnumerateArray());
        Assert.Equal(0, newWal.GetProperty("length").GetInt64());
    }

    [Theory]
    [InlineData("changed-db")]
    [InlineData("prematerialized-wal")]
    [InlineData("prematerialized-shm")]
    public async Task Rehearsal_FirstOpenCannotExcuseCapturedByteChangesOrFabricatedSidecars(string defect)
    {
        using Fixture fixture = new();
        await fixture.CaptureAsync();
        PlannerRetainedRehearsalResult result = await fixture.RunAsync((stage, path) =>
        {
            if (stage != "materialized") return;
            if (defect == "changed-db")
            {
                using SqliteConnection connection = CaptureFixture.Open(path);
                Execute(connection, "UPDATE jira_processing_source_tickets SET Title='unrelated-change'");
            }
            else
            {
                File.WriteAllBytes(path + (defect == "prematerialized-wal" ? "-wal" : "-shm"), []);
            }
        });
        Incomplete(result);
        Assert.Null(result.GuardName);
        Assert.DoesNotContain(result.Stages, stage => stage.Name == "initialization" && stage.Status == "passed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rehearsal_RebuildsShmAndRequiresFirstOpenInventoryEquality(bool logicalDivergence)
    {
        using Fixture fixture = new(createDatabase: false);
        fixture.SeedWal();
        await fixture.CaptureAsync();
        PlannerRetainedArtifact raw = fixture.Manifest!.Artifacts.Single(artifact => artifact.OriginalPath == fixture.Database);
        if (logicalDivergence)
        {
            // A self-consistent hash catalog still cannot assert WAL logical
            // equivalence. Change ONLY synthetic raw evidence, not the safety copy.
            string rawPath = Path.Combine(fixture.Bundle, raw.RawPath.Replace('/', '\\'));
            using (SqliteConnection connection = CaptureFixture.Open(rawPath))
            {
                Execute(connection, "UPDATE wal_only SET Value = 'tampered raw logical value'");
            }
            fixture.RebindRawCatalog();
        }
        bool ownershipContinuous = false;
        bool noShm = false;
        PlannerRetainedRehearsalResult result = await fixture.RunAsync((stage, database) =>
        {
            if (stage == "materialized")
            {
                noShm = !File.Exists(database + "-shm");
            }
            if (stage == "pass1-first-open-validated")
            {
                using PlannerDatabase contender = NewDatabase(database);
                Assert.Throws<InvalidOperationException>(contender.AcquireStartupOwnership);
                ownershipContinuous = true;
            }
        });
        Assert.True(noShm);
        if (logicalDivergence)
        {
            Incomplete(result);
            Assert.Equal("wal-materialization-divergence", result.FailureCategory);
            Assert.DoesNotContain(result.Stages, stage => stage.Name == "initialization" && stage.Status == "passed");
        }
        else
        {
            Passed(result);
            Assert.True(ownershipContinuous);
            Assert.Equal("committed WAL value", fixture.Scalar(result, "SELECT Value FROM wal_only"));
            using JsonDocument family = JsonDocument.Parse(File.ReadAllText(fixture.OutputPath("checkpoints/pass1-first-open/family.json")));
            Assert.True(family.RootElement.GetProperty("dbAndCapturedWalBytesUnchanged").GetBoolean());
            Assert.False(family.RootElement.GetProperty("capturedShmMaterialized").GetBoolean());
            Assert.True(family.RootElement.GetProperty("copyLocalShmPresent").GetBoolean());
        }
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("exclusive-locks")]
    [InlineData("opaque-offline-drive")]
    public async Task Rehearsal_OriginalPathsUnavailableStillSucceeds(string isolation)
    {
        using Fixture fixture = new();
        fixture.Capture.SeedCompleteGraph();
        await fixture.Capture.SeedSnapshotAsync();
        fixture.Activation(true);
        await fixture.CaptureAsync();
        string original = fixture.Capture.Original;
        string moved = Path.Combine(fixture.Root, "unavailable");
        List<FileStream> locks = [];
        Dictionary<string, string> originals = HashTree(original);
        if (isolation is "unavailable" or "opaque-offline-drive")
        {
            Directory.Move(original, moved);
        }
        if (isolation == "exclusive-locks")
        {
            foreach (string path in Directory.GetFiles(original, "*", SearchOption.AllDirectories))
            {
                locks.Add(new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            }
        }
        if (isolation == "opaque-offline-drive")
        {
            fixture.RewriteManifest(manifest =>
            {
                string Remap(string path) => path.Replace(original, @"Z:\opaque-planner-original", StringComparison.Ordinal);
                return manifest with
                {
                    Settings = manifest.Settings with
                    {
                        Database = Remap(manifest.Settings.Database), PreCutoverBackup = Remap(manifest.Settings.PreCutoverBackup),
                        Snapshots = Remap(manifest.Settings.Snapshots),
                    },
                    Roots = manifest.Roots.Select(root => root with { OriginalDirectory = Remap(root.OriginalDirectory) }).ToArray(),
                    Artifacts = manifest.Artifacts.Select(artifact => artifact with { OriginalPath = Remap(artifact.OriginalPath) }).ToArray(),
                };
            });
        }
        Dictionary<string, string> bundle = HashTree(fixture.Bundle);
        (int code, string output, string error) child;
        try
        {
            child = await RunChildAsync(fixture, fixture.Arguments());
        }
        finally
        {
            foreach (FileStream file in locks) file.Dispose();
        }
        Assert.True(child.code == 0, $"{child.code}\n{child.output}\n{child.error}\n{fixture.ResultText()}");
        Assert.Empty(child.error);
        Assert.DoesNotContain("Now listening", child.output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "forbidden")));
        Assert.Equal(bundle, HashTree(fixture.Bundle));
        Assert.Equal(originals, HashTree(isolation == "exclusive-locks" ? original : moved));
        if (isolation != "exclusive-locks") Assert.False(Directory.Exists(original));
        PlannerRetainedRehearsalResult result = fixture.ReadResult();
        Passed(result);
        Assert.All(result.Preservation.Artifacts.Where(artifact => artifact.Path.EndsWith(".descriptor.json", StringComparison.Ordinal)),
            artifact => Assert.Equal("byte-identical", artifact.Disposition));
        Assert.Equal(1L, fixture.Scalar(result, "SELECT Epoch FROM authoring_processor_modes"));
    }

    [Theory]
    [InlineData("map-escape")]
    [InlineData("backup-family-alias")]
    [InlineData("backup-owner-lock-alias")]
    [InlineData("raw-hardlink")]
    [InlineData("overlapping-output")]
    [InlineData("existing-output")]
    [InlineData("expanded-budget")]
    public async Task Rehearsal_RejectsUnmappedOrAliasedPathsBeforeDatabaseOpen(string defect)
    {
        using Fixture fixture = new();
        await fixture.CaptureAsync();
        PlannerRetainedRehearsalRequest request = fixture.Request;
        switch (defect)
        {
            case "map-escape":
                fixture.RewriteManifest(manifest => manifest with
                {
                    SettingsPaths = manifest.SettingsPaths with
                    {
                        Database = manifest.SettingsPaths.Database with { RelativePath = "../outside.db" },
                    },
                });
                break;
            case "backup-family-alias":
            case "backup-owner-lock-alias":
                string suffix = defect == "backup-family-alias" ? "-wal" : ".owner.lock";
                fixture.RewriteManifest(manifest => manifest with
                {
                    Settings = manifest.Settings with { PreCutoverBackup = manifest.Settings.Database + suffix },
                    SettingsPaths = manifest.SettingsPaths with
                    {
                        PreCutoverBackup = manifest.SettingsPaths.Database with
                        {
                            RelativePath = manifest.SettingsPaths.Database.RelativePath + suffix, Existed = false,
                        },
                    },
                });
                break;
            case "raw-hardlink":
                PlannerRetainedArtifact file = fixture.Manifest!.Artifacts.First();
                Assert.True(CreateHardLink(Path.Combine(fixture.Root, "alias.bin"), fixture.BundlePath(file.RawPath), IntPtr.Zero));
                break;
            case "overlapping-output": request = request with { Output = Path.Combine(fixture.Bundle, "work") }; break;
            case "existing-output": Directory.CreateDirectory(fixture.Output); break;
            case "expanded-budget": request = request with { Output = Path.Combine(fixture.Root, new string('x', 160)) }; break;
        }
        bool opened = false;
        await Assert.ThrowsAnyAsync<Exception>(() => new PlannerRetainedStateRehearsal((_, _) => opened = true).RehearseAsync(request));
        Assert.False(opened);
        Assert.False(File.Exists(Path.Combine(request.Output, "rehearsal.started.json")));
        Assert.False(File.Exists(Path.Combine(request.Output, "rehearsal.result.json")));
    }

    [Fact]
    public async Task Rehearsal_DoesNotConstructHostLoadConfigOrUseNetwork()
    {
        using Fixture fixture = new();
        await fixture.CaptureAsync();
        (int code, string output, string error) = await RunChildAsync(fixture, fixture.Arguments());
        Assert.True(code == 0, $"{output}\n{error}\n{fixture.ResultText()}");
        Assert.Empty(error);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "forbidden")));
        Assert.DoesNotContain("Application started", output, StringComparison.Ordinal);

        HashSet<MethodBase> visited = [];
        Queue<MethodBase> pending = new();
        pending.Enqueue(typeof(PlannerRetainedStateRehearsal).GetMethod("RehearseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!);
        pending.Enqueue(typeof(PlannerDatabase).GetMethod(nameof(PlannerDatabase.EnsureSchema))!);
        pending.Enqueue(typeof(SourceDatabase).GetMethod(nameof(SourceDatabase.OpenConnection))!);
        while (pending.TryDequeue(out MethodBase? method))
        {
            if (!visited.Add(method)) continue;
            Type? stateMachine = method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?.StateMachineType ??
                method.GetCustomAttribute<System.Runtime.CompilerServices.IteratorStateMachineAttribute>()?.StateMachineType;
            if (stateMachine?.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is { } move)
            {
                pending.Enqueue(move);
            }
            foreach (MethodBase called in Calls(method))
            {
                string type = called.DeclaringType?.FullName ?? "";
                Assert.False(type.StartsWith("System.Net.", StringComparison.Ordinal), $"Network edge: {method} -> {called}");
                Assert.False(type.StartsWith("Microsoft.Extensions.Configuration.", StringComparison.Ordinal), $"Configuration edge: {called}");
                Assert.False(type.StartsWith("Microsoft.Extensions.DependencyInjection.", StringComparison.Ordinal), $"DI edge: {called}");
                Assert.False(type.StartsWith("Microsoft.Extensions.Hosting.", StringComparison.Ordinal), $"Host edge: {called}");
                Assert.False(type == "Microsoft.AspNetCore.Builder.WebApplication" && called.Name == "CreateBuilder");
                Assert.False(type == "System.Diagnostics.Process", $"Process edge: {called}");
                if (called.Module.Assembly.GetName().Name?.StartsWith("FhirAugury.", StringComparison.Ordinal) == true)
                {
                    pending.Enqueue(called);
                }
            }
        }
        Assert.True(visited.Count > 40, "The actual persistence/recovery/cutover call graph must be traversed.");
        Assert.DoesNotContain(visited, method => method.Name == "GetRunPartitionsAsync");
        Assert.Contains(visited, method => method.Name == "RecoverInterruptedMaintenanceLeasesAsync");
        Assert.Contains(visited, method => method.Name == "ActivateAsync");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("original-option")]
    [InlineData("unknown-option")]
    [InlineData("invalid-commit")]
    [InlineData("positional")]
    [InlineData("wrong-case")]
    public async Task Rehearsal_StrictChildArgumentsAlwaysTerminate(string defect)
    {
        using Fixture fixture = new();
        List<string> arguments = fixture.Arguments().ToList();
        switch (defect)
        {
            case "missing": arguments.RemoveRange(2, 2); break;
            case "duplicate": arguments[4] = "--bundle"; break;
            case "original-option": arguments.AddRange(["--database", fixture.Database]); break;
            case "unknown-option": arguments.AddRange(["--config", "unused"]); break;
            case "invalid-commit": arguments[7] = "HEAD"; break;
            case "positional": arguments.Add("extra"); break;
            case "wrong-case": arguments[1] = "Rehearse"; break;
        }
        (int code, string output, string error) = await RunChildAsync(fixture, arguments);
        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.Contains("invalid-arguments", error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.Output));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "forbidden")));
        Assert.False(File.Exists(fixture.Database + ".owner.lock"));
    }

    [Fact]
    public async Task Rehearsal_ChildHelpDoesNotNeedBundleOrOriginals()
    {
        using Fixture fixture = new(createDatabase: false);
        (int code, string output, string error) = await RunChildAsync(fixture, ["retained-state", "rehearse", "--help"]);
        Assert.Equal(0, code);
        Assert.Equal(PlannerRetainedStateCommand.Help.Trim(), output.Trim());
        Assert.Empty(error);
        Assert.False(Directory.Exists(fixture.Output));
        Assert.False(File.Exists(fixture.Database));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rehearsal_ReportsInitializationRecoveryAndCutoverSeparately(bool timestampRevision)
    {
        using Fixture fixture = new();
        await fixture.SeedLegacyPlanAsync();
        fixture.MakeSourceLegacy();
        if (timestampRevision) fixture.Execute("UPDATE jira_processing_source_tickets SET LastUpdated = @at");
        fixture.Execute("""
            INSERT INTO authoring_mutation_fences(ProcessorKind,RunId,LeaseId,AcquiredAt)
            VALUES('jira-fhir','maintenance:old:grouping','old-lease',@at);
            UPDATE planned_ticket_authoring_state SET LegacyCompletionId='stale', LegacyCompletedProcessingAt='stale';
            """);
        fixture.Activation(true);
        await fixture.CaptureAsync();
        bool postInitBeforeRecovery = false;
        PlannerRetainedRehearsalResult result = await fixture.RunAsync((stage, database) =>
        {
            if (stage == "pass1-post-recovery")
            {
                Assert.True(File.Exists(fixture.OutputPath("checkpoints/pass1-post-initialization/checkpoint.json")));
                postInitBeforeRecovery = true;
            }
            if (stage == "pass1-post-cutover")
            {
                Assert.True(File.Exists(fixture.OutputPath("checkpoints/pass1-post-recovery/checkpoint.json")));
                using PlannerDatabase competing = NewDatabase(database);
                Assert.Throws<InvalidOperationException>(competing.AcquireStartupOwnership);
            }
        });
        Passed(result);
        Assert.True(postInitBeforeRecovery);
        PlannerRetainedRehearsalCheckpoint initialization = Point(result, "pass1-post-initialization");
        PlannerRetainedRehearsalCheckpoint recovery = Point(result, "pass1-post-recovery");
        PlannerRetainedRehearsalCheckpoint cutover = Point(result, "pass1-post-cutover");
        Assert.Contains(initialization.Inventory.Tables.Single(table => table.Name == "jira_processing_source_tickets").Columns,
            column => column.Name == "CompletionId");
        Assert.Equal(1, initialization.Inventory.Tables.Single(table => table.Name == "authoring_mutation_fences").Rows.RowCount);
        Assert.Equal(0, recovery.Inventory.Tables.Single(table => table.Name == "authoring_mutation_fences").Rows.RowCount);
        Assert.Equal(1, cutover.Inventory.Tables.Single(table => table.Name == "authoring_mutation_fences").Rows.RowCount);
        Assert.Equal(1L, fixture.Scalar(result, "SELECT Epoch FROM authoring_processor_modes"));
        Assert.Equal(1L, fixture.Scalar(result, "SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(0L, fixture.Scalar(result, "SELECT COUNT(*) FROM authoring_result_receipts"));
        Assert.IsType<DBNull>(fixture.Scalar(result, "SELECT CompletionId FROM jira_processing_source_tickets"));
        Assert.IsType<DBNull>(fixture.Scalar(result, "SELECT LegacyCompletionId FROM planned_ticket_authoring_state"));
        string revision = Assert.IsType<string>(fixture.Scalar(result, "SELECT ExpectedSourceRevision FROM authoring_run_items"));
        Assert.Equal(timestampRevision ? CaptureFixture.At : AuthoringResultHasher.HashNormalizedUtf8(
            "FHIR-1\nretained title\nResolved - change required\nFHIR Infrastructure\nChange Request\nFHIR"), revision);
        Assert.Contains(result.Preservation.NewWorkFiles, file => file.Path == result.PathMap.PreCutoverBackup);
        Assert.Equal("running", fixture.Scalar(result, "SELECT Status FROM authoring_runs"));
        Assert.Equal("pending", fixture.Scalar(result, "SELECT Status FROM authoring_run_items"));
    }

    [Theory]
    [InlineData("all-maintenance", "grouping-maintenance")]
    [InlineData("ordered-overlap", "grouping-maintenance")]
    [InlineData("uppercase-maintenance", "grouping-maintenance")]
    [InlineData("partial-membership", "authoring")]
    [InlineData("mixed-membership", "authoring")]
    [InlineData("mixed-kinds", "authoring")]
    [InlineData("nonmaintenance-member", "authoring")]
    [InlineData("empty-membership", "authoring")]
    [InlineData("not-database-only", "authoring")]
    [InlineData("mode-linked", "initial-revalidation")]
    [InlineData("lineage-linked", "initial-revalidation")]
    [InlineData("already-purpose", "custom-purpose")]
    public async Task Rehearsal_ClassifiesExistingPurposeBackfillsPrecisely(string scenario, string expected)
    {
        using Fixture fixture = new();
        await fixture.LegacyModeAsync();
        fixture.SeedPurpose(scenario);
        await fixture.CaptureAsync();
        PlannerRetainedRehearsalResult result = await fixture.RunAsync();
        Passed(result);
        Assert.Equal(expected, fixture.Scalar(result, "SELECT Purpose FROM authoring_runs WHERE Id='purpose-run'"));
        Assert.Equal("retained request", fixture.Scalar(result, "SELECT RequestJson FROM authoring_runs WHERE Id='purpose-run'"));
        Assert.Equal("retained run error", fixture.Scalar(result, "SELECT Error FROM authoring_runs WHERE Id='purpose-run'"));
        Assert.Equal(CaptureFixture.At, fixture.Scalar(result, "SELECT CreatedAt FROM authoring_runs WHERE Id='purpose-run'"));
        Assert.All(result.Checkpoints, point => Assert.DoesNotContain(point.Deltas, delta => delta.Status == "unexpected"));
        if (scenario == "lineage-linked")
        {
            Assert.Equal("initial-revalidation", fixture.Scalar(result, "SELECT Purpose FROM authoring_runs WHERE Id='previous-run'"));
        }
    }

    [Theory]
    [InlineData("old-maintenance", 0)]
    [InlineData("uppercase-old-maintenance", 0)]
    [InlineData("current-maintenance", 1)]
    [InlineData("authoring", 1)]
    [InlineData("revalidation", 1)]
    public async Task Rehearsal_RecoveryPreservesEveryOtherFenceCoordinate(string kind, long remaining)
    {
        using Fixture fixture = new();
        await fixture.LegacyModeAsync();
        string run;
        if (kind == "current-maintenance")
        {
            using PlannerDatabase database = NewDatabase(fixture.Database);
            run = Assert.IsType<PlannerMaintenanceLease>(await database.TryAcquireMaintenanceLeaseAsync("fixture")).RunId;
        }
        else
        {
            run = kind switch
            {
                "old-maintenance" => "maintenance:old:fixture",
                "uppercase-old-maintenance" => "MAINTENANCE:old:fixture",
                _ => "fenced-run",
            };
            if (kind is "authoring" or "revalidation")
            {
                fixture.Execute("""
                    INSERT INTO authoring_runs(Id,ProcessorKind,AuthoringEpoch,Status,Purpose,DatabaseOnly,TotalItems,CreatedAt)
                    VALUES('fenced-run','jira-fhir',0,'running',@purpose,0,0,@at)
                    """, ("@purpose", kind == "revalidation" ? "initial-revalidation" : "authoring"));
            }
            fixture.Execute("""
                INSERT INTO authoring_mutation_fences(ProcessorKind,RunId,LeaseId,AcquiredAt)
                VALUES('jira-fhir',@run,'lease-coordinate',@at)
                """, ("@run", run));
        }
        await fixture.CaptureAsync();
        PlannerRetainedRehearsalResult result = await fixture.RunAsync();
        Passed(result);
        Assert.Equal(remaining, fixture.Scalar(result, "SELECT COUNT(*) FROM authoring_mutation_fences"));
        if (remaining == 1)
        {
            Assert.Equal(run, fixture.Scalar(result, "SELECT RunId FROM authoring_mutation_fences"));
            PlannerRetainedRows before = result.Baseline.Tables.Single(table => table.Name == "authoring_mutation_fences").Rows;
            Assert.Equal(before.Sha256, Point(result, "pass2-post-recovery").Inventory.Tables
                .Single(table => table.Name == "authoring_mutation_fences").Rows.Sha256);
        }
    }

    [Theory]
    [InlineData("unknown-value", "pass1-post-initialization", "unknown_values")]
    [InlineData("receipt", "pass1-post-cutover", "authoring_result_receipts")]
    [InlineData("row-identity", "pass1-post-recovery", "jira_processing_source_tickets")]
    [InlineData("epoch", "pass1-post-cutover", "authoring_processor_modes")]
    [InlineData("ineligible-purpose", "pass1-post-initialization", "authoring_runs")]
    [InlineData("other-run-field", "pass1-post-initialization", "authoring_runs")]
    [InlineData("graph", "pass1-post-cutover", "planned_tickets")]
    [InlineData("new-receipt", "pass1-post-cutover", "authoring_result_receipts")]
    public async Task Rehearsal_RejectsUnexpectedReceiptIdentityOrEpochMutation(string mutation, string boundary, string table)
    {
        using Fixture fixture = new();
        fixture.Capture.SeedCompleteGraph();
        fixture.SeedUnknown();
        fixture.Activation(true);
        await fixture.CaptureAsync();
        Dictionary<string, string> bundle = HashTree(fixture.Bundle);
        bool injected = false;
        PlannerRetainedRehearsalResult result = await fixture.RunAsync((stage, database) =>
        {
            if (stage != boundary) return;
            injected = true;
            using SqliteConnection connection = CaptureFixture.Open(database);
            Execute(connection, mutation switch
            {
                "unknown-value" => "UPDATE unknown_values SET Value=x'01' WHERE rowid=1",
                "receipt" => """
                    UPDATE authoring_result_receipts SET ContentHash='unrelated-change';
                    UPDATE authoring_run_attempts SET ContentHash='unrelated-change';
                    UPDATE planned_ticket_authoring_state SET ReceiptContentHash='unrelated-change';
                    """,
                "row-identity" => "UPDATE jira_processing_source_tickets SET RowId=71",
                "epoch" => """
                    UPDATE authoring_processor_modes SET Epoch=Epoch+1;
                    UPDATE authoring_runs SET AuthoringEpoch=AuthoringEpoch+1;
                    UPDATE authoring_result_receipts SET AuthoringEpoch=AuthoringEpoch+1;
                    """,
                "ineligible-purpose" => "UPDATE authoring_runs SET Purpose='initial-revalidation'",
                "other-run-field" => "UPDATE authoring_runs SET Error='unrelated-change'",
                "graph" => "UPDATE planned_tickets SET FeatureProposal='unrelated-change'",
                _ => """
                    INSERT INTO authoring_result_receipts(Id,OperationId,RunId,RunItemId,BusinessKey,ContentHash,
                        ExpectedSourceRevision,ObservedSourceRevision,AuthoringEpoch,PersistedAt)
                    SELECT 'new-receipt','new-operation',RunId,RunItemId,BusinessKey,ContentHash,
                        ExpectedSourceRevision,ObservedSourceRevision,AuthoringEpoch,PersistedAt FROM authoring_result_receipts LIMIT 1
                    """,
            });
        });
        Assert.True(injected);
        Incomplete(result);
        Assert.Contains(result.Checkpoints.SelectMany(point => point.Deltas), delta => delta.Table == table && delta.Status == "unexpected");
        Assert.Equal(bundle, HashTree(fixture.Bundle));
        Assert.DoesNotContain(result.Stages, stage => stage.Pass == 2 && stage.Status == "passed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rehearsal_ExistingDifferentBackupProducesCompleteBlockedEvidence(bool legacySource)
    {
        using Fixture fixture = new();
        await fixture.SeedLegacyPlanAsync();
        if (legacySource) fixture.MakeSourceLegacy();
        fixture.Backup();
        fixture.Execute("UPDATE jira_processing_source_tickets SET Description='committed after backup'");
        fixture.Activation(true);
        await fixture.CaptureAsync();
        string hash = CaptureFixture.Hash(fixture.BackupPath);
        Dictionary<string, string> bundle = HashTree(fixture.Bundle);
        int diagnosticCount = 0;
        PlannerRetainedRehearsalResult result = await fixture.RunAsync((stage, _) =>
        {
            if (stage == "before-backup-diagnostic") diagnosticCount++;
        });
        Blocked(result, "backup-hash-mismatch");
        Assert.Equal(1, diagnosticCount);
        PlannerRetainedRehearsalGuard guard = Assert.IsType<PlannerRetainedRehearsalGuard>(result.Guard);
        Assert.Equal(hash, guard.PreservedBackup!.Sha256);
        Assert.Equal(new FileInfo(fixture.BackupPath).Length, guard.PreservedBackup.Length);
        Assert.StartsWith("legacy;epoch=0;", guard.BackupMode);
        Assert.NotNull(guard.RegeneratedCandidate);
        Assert.NotEqual(guard.PreservedBackup.Sha256, guard.RegeneratedCandidate.Sha256);
        Assert.Equal("separately-regenerated-BackupDatabase;not-coordinator-temp", guard.CandidateAuthority);
        Assert.Equal(guard.RegeneratedCandidate.Sha256, CaptureFixture.Hash(fixture.OutputPath(guard.RegeneratedCandidate.Path)));
        Assert.Equal(hash, CaptureFixture.Hash(fixture.OutputPath(result.PathMap.PreCutoverBackup!)));
        Assert.Equal(bundle, HashTree(fixture.Bundle));
        Assert.Equal("legacy", fixture.Scalar(result, "SELECT Mode FROM authoring_processor_modes"));
        Assert.Equal(0L, fixture.Scalar(result, "SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(0L, fixture.Scalar(result, "SELECT COUNT(*) FROM authoring_result_receipts"));
        Assert.Contains(Point(result, "pass1-failure-state").Inventory.Tables.Single(table => table.Name == "jira_processing_source_tickets").Columns,
            column => column.Name == "CompletionId");
        Assert.All(result.Stages.Where(stage => stage.Pass == 2), stage => Assert.Equal("not-run-after-refusal", stage.Status));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("cancelled")]
    [InlineData("diagnostic-failure")]
    [InlineData("competing-owner")]
    public async Task Rehearsal_IncompleteOrUnknownFailureCannotCompleteEvidence(string failure)
    {
        using Fixture fixture = new();
        await fixture.LegacyModeAsync();
        if (failure == "diagnostic-failure")
        {
            fixture.Backup();
            fixture.Execute("UPDATE jira_processing_source_tickets SET Description='different'");
            fixture.Activation(true);
        }
        await fixture.CaptureAsync();
        FileStream? owner = null;
        PlannerRetainedRehearsalResult result;
        try
        {
            result = await fixture.RunAsync((stage, database) =>
            {
                if (failure == "competing-owner" && stage == "pass1-before-ownership")
                {
                    owner = new(database + ".owner.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                if (failure == "unknown" && stage == "pass1-post-recovery")
                {
                    throw new InvalidOperationException("An existing pre-cutover backup does not match the current committed legacy database.");
                }
                if (failure == "cancelled" && stage == "pass1-post-initialization") throw new OperationCanceledException();
                if (failure == "diagnostic-failure" && stage == "before-backup-diagnostic") throw new IOException("synthetic diagnostic failure");
            });
        }
        finally
        {
            owner?.Dispose();
        }
        Incomplete(result);
        Assert.Null(result.GuardName);
        Assert.DoesNotContain(result.Stages, stage => stage.Pass == 2 && stage.Status == "passed");
        Assert.NotEmpty(result.FailureDetail!);
        Assert.NotEqual("guarded-refusal", result.StartupOutcome);
    }

    [Theory]
    [InlineData("missing-mode", "backup-hash-mismatch", "legacy")]
    [InlineData("missing-source", "missing-revalidation-source", "cutting-over")]
    [InlineData("cutting-over-missing", "backup-missing-or-invalid", "cutting-over")]
    [InlineData("cutting-over-nonlegacy", "backup-missing-or-invalid", "cutting-over")]
    [InlineData("unknown-mode", "unknown-processor-mode", "future-mode")]
    public async Task Rehearsal_HonorsMissingModeAndCuttingOverFailureBoundaries(string scenario, string guard, string mode)
    {
        using Fixture fixture = new();
        await fixture.SeedLegacyPlanAsync();
        if (scenario == "missing-mode")
        {
            fixture.Backup();
            fixture.Execute("DELETE FROM authoring_processor_modes");
        }
        if (scenario == "missing-source") fixture.Execute("DELETE FROM jira_processing_source_tickets");
        if (scenario == "cutting-over-nonlegacy")
        {
            fixture.Backup();
            using SqliteConnection backup = CaptureFixture.Open(fixture.BackupPath);
            Execute(backup, "UPDATE authoring_processor_modes SET Mode='run-backed'");
        }
        if (scenario.StartsWith("cutting-over", StringComparison.Ordinal)) fixture.Execute("UPDATE authoring_processor_modes SET Mode='cutting-over'");
        if (scenario == "unknown-mode") fixture.Execute("UPDATE authoring_processor_modes SET Mode='future-mode'");
        fixture.Activation(true);
        await fixture.CaptureAsync();
        PlannerRetainedRehearsalResult result = await fixture.RunAsync();
        Blocked(result, guard);
        Assert.Equal(mode, fixture.Scalar(result, "SELECT Mode FROM authoring_processor_modes"));
        Assert.Equal(0L, fixture.Scalar(result, "SELECT Epoch FROM authoring_processor_modes"));
        Assert.Equal(0L, fixture.Scalar(result, "SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(0L, fixture.Scalar(result, "SELECT COUNT(*) FROM authoring_result_receipts"));
        if (scenario == "missing-mode")
        {
            Assert.Equal(0, Point(result, "pass1-post-recovery").Inventory.Tables.Single(table => table.Name == "authoring_processor_modes").Rows.RowCount);
            Assert.Equal(1, Point(result, "pass1-failure-state").Inventory.Tables.Single(table => table.Name == "authoring_processor_modes").Rows.RowCount);
        }
        if (scenario == "missing-source")
        {
            Assert.Contains(result.Preservation.NewWorkFiles, file => file.Path == result.PathMap.PreCutoverBackup);
            Assert.Equal(
                Point(result, "pass1-post-recovery").Inventory.Tables.Single(table => table.Name == "planned_ticket_authoring_state").Rows.Sha256,
                Point(result, "pass1-failure-state").Inventory.Tables.Single(table => table.Name == "planned_ticket_authoring_state").Rows.Sha256);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rehearsal_RunBackedAndSecondPassDoNotAllocateAgain(bool activate)
    {
        using Fixture fixture = new();
        await fixture.SeedReceiptAsync();
        fixture.Activation(activate);
        await fixture.CaptureAsync();
        PlannerRetainedRehearsalResult result = await fixture.RunAsync();
        Passed(result);
        foreach (string table in new[]
        {
            "authoring_processor_modes", "authoring_runs", "authoring_run_items", "authoring_result_receipts",
            "planned_ticket_authoring_state", "jira_processing_source_tickets", "planned_tickets",
        })
        {
            string hash = result.Baseline.Tables.Single(value => value.Name == table).Rows.Sha256;
            Assert.All(result.Checkpoints, point => Assert.Equal(hash, point.Inventory.Tables.Single(value => value.Name == table).Rows.Sha256));
        }
        Assert.False(File.Exists(fixture.OutputPath(result.PathMap.PreCutoverBackup!)));
        Assert.Equal(1L, fixture.Scalar(result, "SELECT COUNT(*) FROM authoring_result_receipts"));
    }

    [Fact]
    public async Task Rehearsal_CuttingOverValidBackupActivatesWithoutHashSubstitution()
    {
        using Fixture fixture = new();
        await fixture.SeedLegacyPlanAsync();
        fixture.Backup();
        fixture.Execute("UPDATE authoring_processor_modes SET Mode='cutting-over'");
        fixture.Activation(true);
        await fixture.CaptureAsync();
        string backup = CaptureFixture.Hash(fixture.BackupPath);
        PlannerRetainedRehearsalResult result = await fixture.RunAsync();
        Passed(result);
        Assert.Equal(backup, CaptureFixture.Hash(fixture.OutputPath(result.PathMap.PreCutoverBackup!)));
        Assert.Equal(1L, fixture.Scalar(result, "SELECT Epoch FROM authoring_processor_modes"));
        Assert.Equal(1L, fixture.Scalar(result, "SELECT COUNT(*) FROM authoring_runs"));
    }

    [Fact]
    public async Task Rehearsal_ActivationPreservesCurrentReceiptBackedGraph()
    {
        using Fixture fixture = new();
        await fixture.SeedReceiptAsync();
        // Synthetic retained legacy metadata with real epoch-zero receipt state.
        // No receipt, source, graph or run value is edited to build this case.
        fixture.Execute("UPDATE authoring_processor_modes SET Mode='legacy'");
        fixture.Activation(true);
        await fixture.CaptureAsync();
        PlannerRetainedRehearsalResult result = await fixture.RunAsync();
        Passed(result);
        Assert.Equal(1L, fixture.Scalar(result, "SELECT Epoch FROM authoring_processor_modes"));
        Assert.Equal(1L, fixture.Scalar(result, "SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(1L, fixture.Scalar(result, "SELECT COUNT(*) FROM authoring_result_receipts"));
        Assert.Equal(0L, fixture.Scalar(result, "SELECT RevalidationRequired FROM authoring_processor_modes"));
        Assert.Equal(result.Baseline.Tables.Single(table => table.Name == "planned_ticket_authoring_state").Rows.Sha256,
            Point(result, "pass1-post-cutover").Inventory.Tables.Single(table => table.Name == "planned_ticket_authoring_state").Rows.Sha256);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("tree")]
    [InlineData("bundle-bytes")]
    [InlineData("binary-fingerprint")]
    [InlineData("missing-marker")]
    public async Task Rehearsal_CandidateOrBundleMismatchRefusesBeforeMutation(string mismatch)
    {
        using Fixture fixture = new();
        await fixture.CaptureAsync();
        PlannerRetainedRehearsalRequest request = fixture.Request;
        switch (mismatch)
        {
            case "commit": request = request with { CandidateCommit = new string('c', 40) }; break;
            case "tree": request = request with { CandidateTree = new string('d', 40) }; break;
            case "bundle-bytes": File.AppendAllText(fixture.BundlePath("capture.started.json"), " "); break;
            case "binary-fingerprint":
                fixture.RewriteManifest(manifest => manifest with
                {
                    Binaries = manifest.Binaries.Select((binary, index) => index == 0 ? binary with { Sha256 = new string('0', 64) } : binary).ToArray(),
                });
                break;
            case "missing-marker": File.Delete(fixture.BundlePath("capture.complete.json")); break;
        }
        bool persisted = false;
        await Assert.ThrowsAsync<PlannerRetainedStateException>(() =>
            new PlannerRetainedStateRehearsal((_, _) => persisted = true).RehearseAsync(request));
        Assert.False(persisted);
        Assert.False(Directory.Exists(fixture.Output));
    }

    [Theory]
    [InlineData("noncanonical")]
    [InlineData("column-contract")]
    [InlineData("missing-required-column")]
    public async Task Rehearsal_SchemaGuardRequiresRealAdmissionAndUnchangedFailureState(string defect)
    {
        using Fixture fixture = new();
        fixture.Execute(defect switch
        {
            "noncanonical" => "UPDATE jira_processing_source_tickets SET Key='fhir-1'",
            "column-contract" => "ALTER TABLE jira_processing_source_tickets ADD COLUMN Unexpected TEXT NOT NULL DEFAULT ''",
            _ => "DROP TRIGGER trg_planned_ticket_receipt_applier_projection; ALTER TABLE jira_processing_source_tickets DROP COLUMN ErrorOccurredAt",
        });
        if (defect == "column-contract")
        {
            fixture.Execute("CREATE UNIQUE INDEX fixture_unrelated_restrictive ON jira_processing_source_tickets(Title)");
        }
        await fixture.CaptureAsync();
        PlannerRetainedRehearsalResult result = await fixture.RunAsync();
        Blocked(result, "schema-admission-refused");
        Assert.StartsWith("JiraProcessingSourceTicketStore.Validate", result.Guard!.Source);
        PlannerRetainedRehearsalCheckpoint failed = Point(result, "pass1-failure-state");
        Assert.Equal(result.Baseline.Tables.Single(table => table.Name == "jira_processing_source_tickets").Rows.Sha256,
            failed.Inventory.Tables.Single(table => table.Name == "jira_processing_source_tickets").Rows.Sha256);
        Assert.All(result.Stages.Where(stage => stage.Name is "recovery" or "cutover"), stage => Assert.Equal("not-run-after-refusal", stage.Status));
    }

    private static PlannerDatabase NewDatabase(string path) => new(path, NullLogger<PlannerDatabase>.Instance);
    private static PlannerRetainedRehearsalCheckpoint Point(PlannerRetainedRehearsalResult result, string name) =>
        Assert.Single(result.Checkpoints, point => point.Name == name);
    private static void Passed(PlannerRetainedRehearsalResult result)
    {
        Assert.True(result.ExitCode == 0, Diagnostics(result));
        Assert.Equal("passed", result.Status);
        Assert.Equal("complete", result.EvidenceStatus);
        Assert.Equal("passed", result.StartupOutcome);
        Assert.Null(result.GuardName);
        Assert.Equal(10, result.Checkpoints.Count);
        Assert.All(result.Checkpoints, point => Assert.Equal("passed", point.Status));
        Assert.All(result.Stages, stage => Assert.Equal("passed", stage.Status));
        Assert.Equal(1, (result with { StartupOutcome = "not-completed" }).ExitCode);
        Assert.Equal(1, (result with { EvidenceStatus = "incomplete" }).ExitCode);
        Assert.Equal(1, (result with { Status = "blocked", StartupOutcome = "guarded-refusal", GuardName = "unrecognized" }).ExitCode);
    }
    private static void Blocked(PlannerRetainedRehearsalResult result, string guard)
    {
        Assert.True(result.ExitCode == 20, Diagnostics(result));
        Assert.Equal("blocked", result.Status);
        Assert.Equal("complete", result.EvidenceStatus);
        Assert.Equal("guarded-refusal", result.StartupOutcome);
        Assert.Equal(guard, result.GuardName);
        Assert.Equal(guard, result.Guard!.Name);
        Assert.All(result.Checkpoints, point => Assert.Equal("passed", point.Status));
        Assert.Equal(1, (result with { GuardName = "unrecognized" }).ExitCode);
        Assert.Equal(1, (result with { Guard = null }).ExitCode);
        Assert.Equal(1, (result with { StartupOutcome = "passed" }).ExitCode);
        Assert.Equal(1, (result with { EvidenceStatus = "incomplete" }).ExitCode);
    }
    private static void Incomplete(PlannerRetainedRehearsalResult result)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("incomplete", result.Status);
        Assert.Equal("incomplete", result.EvidenceStatus);
        Assert.NotEqual("passed", result.StartupOutcome);
    }
    private static string Diagnostics(PlannerRetainedRehearsalResult result) => JsonSerializer.Serialize(new
    {
        result.Status, result.FailureCategory, result.FailureDetail, result.Guard,
        Unexpected = result.Checkpoints.SelectMany(point => point.Deltas)
            .Where(delta => delta.Status == "unexpected").Select(delta => new { delta.Table, delta.Rule }),
    });

    [Fact]
    public void Rehearsal_DeclaredSchemaCatalogMatchesCurrentOwner()
    {
        using Fixture fixture = new();
        Type type = typeof(PlannerRetainedStateRehearsal);
        var contracts = (System.Collections.IDictionary)type.GetField("SchemaContract", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        MethodInfo normalize = type.GetMethod("NormalizeSql", BindingFlags.Static | BindingFlags.NonPublic)!;
        using SqliteConnection connection = CaptureFixture.Open(fixture.Database, SqliteOpenMode.ReadOnly);
        foreach (System.Collections.DictionaryEntry entry in contracts)
        {
            string? expected = (string?)entry.Value!.GetType().GetProperty("Sql")!.GetValue(entry.Value);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE name=@name COLLATE NOCASE";
            command.Parameters.AddWithValue("@name", entry.Key);
            object? actual = command.ExecuteScalar();
            Assert.True(actual is not null, $"Missing declared object: {entry.Key}");
            if (expected is not null)
            {
                string left = (string)normalize.Invoke(null, [expected])!;
                string right = (string)normalize.Invoke(null, [Assert.IsType<string>(actual)])!;
                Assert.True(left == right, $"{entry.Key}\nexpected: {left}\nactual: {right}");
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal CaptureFixture Capture { get; }
        internal string Root => Capture.Root;
        internal string Database => Capture.Database;
        internal string Bundle => Capture.Settings.Bundle;
        internal string Output => Path.Combine(Root, "rehearsal-1");
        internal string BackupPath => Path.Combine(Capture.Original, "pre.db");
        internal PlannerRetainedStateManifest? Manifest { get; private set; }
        internal PlannerRetainedRehearsalRequest Request => new(Bundle, Output, CaptureFixture.Commit, CaptureFixture.Tree);
        internal Fixture(bool createDatabase = true)
        {
            Capture = new(createDatabase, currentSchema: true);
            Capture.Settings = Capture.Settings with { PreCutoverBackup = BackupPath };
        }
        internal void Activation(bool value) => Capture.Settings = Capture.Settings with { ActivateRunBackedAuthoring = value };
        internal async Task LegacyModeAsync()
        {
            using PlannerDatabase database = NewDatabase(Database);
            await new AuthoringRunStore(database).EnsureProcessorModeAsync("jira-fhir", now: DateTimeOffset.Parse(CaptureFixture.At, CultureInfo.InvariantCulture));
        }
        internal async Task CaptureAsync() => Manifest = await new PlannerRetainedStateCapture().CaptureAsync(Capture.Settings);
        internal Task<PlannerRetainedRehearsalResult> RunAsync(Action<string, string>? checkpoint = null) =>
            new PlannerRetainedStateRehearsal(checkpoint).RehearseAsync(Request);
        internal string OutputPath(string relative) => Path.Combine(Output, relative.Replace('/', '\\'));
        internal string BundlePath(string relative) => Path.Combine(Bundle, relative.Replace('/', '\\'));
        internal string ResultText() => File.Exists(OutputPath("rehearsal.result.json")) ? Diagnostics(ReadResult()) : "No result.";
        internal PlannerRetainedRehearsalResult ReadResult() =>
            PlannerRetainedStateEvidence.ReadJson<PlannerRetainedRehearsalResult>(OutputPath("rehearsal.result.json"));
        internal string[] Arguments() =>
        [
            "retained-state", "rehearse", "--bundle", Bundle, "--output", Output,
            "--candidate-commit", CaptureFixture.Commit, "--candidate-tree", CaptureFixture.Tree,
        ];
        internal void Execute(string sql, params (string Name, object? Value)[] values)
        {
            using SqliteConnection connection = CaptureFixture.Open(Database);
            PlannerRetainedStateRehearsalTests.Execute(connection, sql, values.Prepend(("@at", (object?)CaptureFixture.At)).ToArray());
        }
        internal object? Scalar(PlannerRetainedRehearsalResult result, string sql)
        {
            using SqliteConnection connection = CaptureFixture.Open(OutputPath(result.PathMap.Database), SqliteOpenMode.ReadOnly);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
        internal void Backup()
        {
            using SqliteConnection source = CaptureFixture.Open(Database);
            using SqliteConnection backup = CaptureFixture.Open(BackupPath);
            source.BackupDatabase(backup);
        }
        internal async Task SeedLegacyPlanAsync()
        {
            await LegacyModeAsync();
            using PlannerDatabase database = NewDatabase(Database);
            await database.SavePlannedTicketAsync(Payload());
        }
        internal void MakeSourceLegacy()
        {
            // Independently handwritten reported shape, not generated source DDL.
            Execute("""
                DROP TABLE jira_processing_source_tickets;
                CREATE TABLE jira_processing_source_tickets(
                    RowId INTEGER UNIQUE PRIMARY KEY NOT NULL, Id TEXT UNIQUE NOT NULL,
                    Key TEXT NOT NULL, Title TEXT NOT NULL, Description TEXT, Project TEXT NOT NULL,
                    Status TEXT NOT NULL, WorkGroup TEXT NOT NULL, Type TEXT NOT NULL,
                    SourceTicketShape TEXT NOT NULL, LastSyncedAt TEXT NOT NULL, LastUpdated TEXT,
                    StartedProcessingAt TEXT, CompletedProcessingAt TEXT, LastProcessingAttemptAt TEXT,
                    ProcessingStatus TEXT, ProcessingError TEXT, ProcessingAttemptCount INTEGER NOT NULL,
                    ErrorMessage TEXT, AgentExitCode INTEGER, ErrorOccurredAt TEXT, Specification TEXT NOT NULL,
                    SourceProjectLastSuccessfulRefreshAt TEXT, SourceContentRevision INTEGER);
                INSERT INTO jira_processing_source_tickets(RowId,Id,Key,Title,Description,Project,Status,WorkGroup,Type,
                    SourceTicketShape,LastSyncedAt,ProcessingStatus,ProcessingAttemptCount,Specification)
                VALUES(27,'legacy-id','FHIR-1','retained title','retained description','FHIR','Resolved - change required',
                    'FHIR Infrastructure','Change Request','fhir',@at,'complete',3,'FHIR');
                """);
        }
        internal void SeedUnknown() => Execute("""
            CREATE TABLE unknown_values(Id, Value);
            INSERT INTO unknown_values VALUES(NULL,NULL),(1,x'00ff'),(1,x'00ff');
            CREATE TABLE unknown_keyed(Key TEXT PRIMARY KEY, Value BLOB) WITHOUT ROWID;
            INSERT INTO unknown_keyed VALUES('retained',zeroblob(131073));
            """);
        internal void SeedWal()
        {
            string donor = Path.Combine(Root, "donor.db");
            using (PlannerDatabase database = NewDatabase(donor)) database.Initialize();
            using SqliteConnection writer = CaptureFixture.Open(donor);
            PlannerRetainedStateRehearsalTests.Execute(writer, """
                PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;
                CREATE TABLE wal_only(Id INTEGER PRIMARY KEY,Value TEXT);
                INSERT INTO wal_only VALUES(1,'committed WAL value');
                """);
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                using FileStream source = new(donor + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using FileStream copy = new(Database + suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                source.CopyTo(copy);
            }
            using FileStream shm = new(Database + "-shm", FileMode.Open, FileAccess.Write, FileShare.None);
            shm.Write(new byte[64]);
        }
        internal void SeedPurpose(string scenario)
        {
            int members = scenario == "empty-membership" ? 0 : scenario is "mixed-membership" or "mixed-kinds" ? 2 : 1;
            int databaseOnly = scenario is "not-database-only" or "mode-linked" or "lineage-linked" ? 0 : 1;
            Execute("""
                INSERT INTO authoring_runs(Id,ProcessorKind,AuthoringEpoch,Status,Purpose,DatabaseOnly,TotalItems,CreatedAt,Error,RequestJson)
                VALUES('purpose-run','jira-fhir',0,'completed',@purpose,@databaseOnly,@members,@at,'retained run error','retained request')
                """, ("@purpose", scenario == "already-purpose" ? "custom-purpose" : "authoring"),
                ("@databaseOnly", databaseOnly), ("@members", members));
            if (members != 0)
            {
                Execute("""
                    INSERT INTO authoring_run_items(Id,RunId,BusinessKey,ItemKind,ExpectedSourceRevision,Status,AttemptCount,CreatedAt)
                    VALUES('purpose-item','purpose-run','fixture-key',@kind,'retained-revision',@status,2,@at)
                    """, ("@kind", scenario == "nonmaintenance-member" ? "fhir" :
                        scenario == "uppercase-maintenance" ? "MAINTENANCE:grouping" : "maintenance:grouping"),
                    ("@status", scenario == "partial-membership" ? "pending" : "complete"));
            }
            if (members == 2)
            {
                Execute("""
                    INSERT INTO authoring_run_items(Id,RunId,BusinessKey,ItemKind,ExpectedSourceRevision,Status,AttemptCount,CreatedAt)
                    VALUES('second-purpose-item','purpose-run','second-fixture-key',@kind,'second-revision',@status,1,@at)
                    """, ("@kind", scenario == "mixed-kinds" ? "fhir" : "maintenance:grouping"),
                    ("@status", scenario == "mixed-membership" ? "pending" : "complete"));
            }
            if (scenario is "mode-linked" or "ordered-overlap")
            {
                Execute("UPDATE authoring_processor_modes SET RevalidationRunId='purpose-run',RevalidationRequired=1");
            }
            if (scenario == "lineage-linked")
            {
                Execute("""
                    INSERT INTO authoring_runs(Id,ProcessorKind,AuthoringEpoch,Status,Purpose,DatabaseOnly,TotalItems,CreatedAt)
                    VALUES('previous-run','jira-fhir',0,'completed','authoring',0,0,@at);
                    INSERT INTO authoring_revalidation_lineage VALUES('purpose-run','previous-run');
                    """);
            }
        }
        internal async Task SeedReceiptAsync()
        {
            await LegacyModeAsync();
            Execute("UPDATE authoring_processor_modes SET Mode='run-backed'");
            using PlannerDatabase database = NewDatabase(Database);
            AuthoringRunStore store = new(database);
            string revision = AuthoringResultHasher.HashNormalizedUtf8(
                "FHIR-1\nretained title\nResolved - change required\nFHIR Infrastructure\nChange Request\nFHIR");
            AuthoringRunRecord run = await store.CreateRunAsync("jira-fhir", [new("FHIR-1", "fhir", revision)]);
            Assert.True(await store.TryAcquireMutationFenceAsync("jira-fhir", run.Id));
            AuthoringRunItemRecord item = Assert.Single(await store.GetRunItemsAsync(run.Id));
            AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(await store.ClaimItemAsync(run.Id, item.Id));
            PlannedTicketPayload payload = Payload();
            string hash = PlannedTicketAuthoringDtos.ComputeContentHash(payload);
            AuthoringReceiptAcceptance acceptance = await store.AcceptResultAsync(
                new AuthoringResultSubmission(run.Id, item.Id, claim.OperationId, revision, hash),
                claim.OperationToken, async (connection, ct) =>
                {
                    await JiraProcessingSourceTicketStore.EnsureCurrentSourceRevisionAsync(connection, "FHIR-1", "fhir", revision, ct);
                    await database.SavePlannedTicketForAuthoringAsync(connection, payload, hash, run.Id, item.Id, claim.OperationId, ct);
                });
            await store.MarkItemCompleteAsync(item.Id, acceptance.Receipt.ReceiptId);
            Assert.Equal(0, await database.ClassifyLegacyPlannedTicketsAsync());
        }
        internal void RebindRawCatalog()
        {
            RewriteManifest(manifest =>
            {
                PlannerRetainedArtifact[] artifacts = manifest.Artifacts.Where(artifact => File.Exists(BundlePath(artifact.RawPath))).Select(artifact =>
                {
                    string path = BundlePath(artifact.RawPath);
                    return artifact with { Length = new FileInfo(path).Length, Sha256 = CaptureFixture.Hash(path) };
                }).ToArray();
                return manifest with
                {
                    Artifacts = artifacts,
                    Sidecars = manifest.Sidecars.Select(sidecar =>
                        sidecar.ArtifactId is not null && !artifacts.Any(artifact => artifact.Id == sidecar.ArtifactId)
                            ? sidecar with { ArtifactId = null } : sidecar).ToArray(),
                };
            });
        }
        internal void RewriteManifest(Func<PlannerRetainedStateManifest, PlannerRetainedStateManifest> change)
        {
            string manifestPath = BundlePath("manifest.json");
            Manifest = change(PlannerRetainedStateEvidence.ReadJson<PlannerRetainedStateManifest>(manifestPath));
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(Manifest, PlannerRetainedStateEvidence.Json));
            string marker = BundlePath("capture.complete.json");
            PlannerRetainedStateCompletion completion = PlannerRetainedStateEvidence.ReadJson<PlannerRetainedStateCompletion>(marker);
            PlannerRetainedEvidenceFile[] files = Directory.GetFiles(Bundle, "*", SearchOption.AllDirectories)
                .Where(path => path != marker).Select(path => PlannerRetainedStateEvidence.Fingerprint(Bundle, path))
                .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
            completion = completion with
            {
                ManifestSha256 = CaptureFixture.Hash(manifestPath), Files = files, Binaries = Manifest.Binaries,
            };
            File.WriteAllText(marker, JsonSerializer.Serialize(completion, PlannerRetainedStateEvidence.Json));
        }
        public void Dispose() => Capture.Dispose();
    }

    private static PlannedTicketPayload Payload() => new()
    {
        Key = "FHIR-1", Resolution = "Persuasive", ResolutionSummary = "retained summary",
        FeatureProposal = "retained proposal", DesignRationale = "retained rationale",
        SavedAt = DateTimeOffset.Parse(CaptureFixture.At, CultureInfo.InvariantCulture),
        Repos = [new() { RepoKey = "HL7/fhir", RepoRevision = "abc", Justification = "retained justification" }],
    };

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] values)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    private static Dictionary<string, string> HashTree(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).ToDictionary(path => Path.GetRelativePath(root, path), CaptureFixture.Hash, StringComparer.Ordinal);

    private static async Task<(int Code, string Output, string Error)> RunChildAsync(Fixture fixture, IEnumerable<string> arguments)
    {
        File.WriteAllText(Path.Combine(fixture.Root, "appsettings.json"), "{ poison configuration");
        File.WriteAllText(Path.Combine(fixture.Root, "appsettings.Poisoned.json"), "{ poison configuration");
        string runtime = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        string dotnetRoot = Directory.GetParent(runtime)!.Parent!.Parent!.FullName;
        ProcessStartInfo start = new(Path.Combine(dotnetRoot, "dotnet.exe"))
        {
            WorkingDirectory = fixture.Root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(PlannerRetainedStateCommand).Assembly.Location);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (string name in new[] { "SystemRoot", "WINDIR" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) start.Environment[name] = value;
        }
        start.Environment["TEMP"] = Path.Combine(fixture.Output, "temp");
        start.Environment["TMP"] = start.Environment["TEMP"];
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Poisoned";
        start.Environment["ASPNETCORE_URLS"] = "not-a-listening-address";
        start.Environment["FHIR_AUGURY_PROCESSOR_JIRA_FHIR_PLANNER_Processing__DatabasePath"] = Path.Combine(fixture.Root, "forbidden", "service.db");
        start.Environment["FHIR_AUGURY_PROCESSOR_JIRA_FHIR_PLANNER_Processing__OrchestratorAddress"] = "not-a-network-address";
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Child did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Retained-state child did not terminate.");
        }
        return (process.ExitCode, await output, await error);
    }

    private static IEnumerable<MethodBase> Calls(MethodBase method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) yield break;
        Dictionary<short, OpCode> codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => code.Value);
        for (int index = 0; index < il.Length;)
        {
            short value = il[index++] == 0xfe ? (short)(0xfe00 | il[index++]) : il[index - 1];
            OpCode code = codes[value];
            int length = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, index),
                _ => 4,
            };
            if (code.OperandType == OperandType.InlineMethod)
            {
                MethodBase? called;
                try
                {
                    called = method.Module.ResolveMethod(BitConverter.ToInt32(il, index),
                        method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                catch (ArgumentException)
                {
                    called = null;
                }
                if (called is not null) yield return called;
            }
            index += length;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string target, IntPtr security);
}
