using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Maintenance;

internal sealed class PlannerRetainedStateRehearsal(Action<string, string>? checkpoint = null)
{
    private const string SourceTable = "jira_processing_source_tickets";
    private const string ModeTable = "authoring_processor_modes";
    private const string StateTable = "planned_ticket_authoring_state";
    private const string RunsTable = "authoring_runs";
    private const string ItemsTable = "authoring_run_items";
    private const string FencesTable = "authoring_mutation_fences";
    private static readonly string[] StageNames = ["ownership", "first-open", "initialization", "recovery", "cutover", "reads"];

    private sealed record Snapshot(string Root, string Name, PlannerRetainedDatabaseInventory Inventory)
    {
        internal string Database => PlannerRetainedStatePaths.Under(Root, Inventory.SafetyPath);
    }

    private sealed class FilePins : IDisposable
    {
        private readonly List<IDisposable> _handles = [];
        internal void Add(IDisposable handle) => _handles.Add(handle);
        public void Dispose()
        {
            foreach (IDisposable handle in _handles.AsEnumerable().Reverse()) handle.Dispose();
            _handles.Clear();
        }
    }

    internal async Task<PlannerRetainedRehearsalResult> RehearseAsync(
        PlannerRetainedRehearsalRequest request, CancellationToken ct = default, bool configureProcessTemp = false)
    {
        if (!PlannerRetainedStateCommand.IsObjectId(request.CandidateCommit) ||
            !PlannerRetainedStateCommand.IsObjectId(request.CandidateTree))
        {
            throw new ArgumentException("Full candidate identities are required.");
        }
        // The lease, not the marker's existence, is the input authority. It stays
        // alive through final comparison and result publication.
        using PlannerRetainedVerifiedCapture capture = await PlannerRetainedStateCapture.OpenVerifiedAsync(
            request.Bundle, request.CandidateCommit, request.CandidateTree, ct);
        PlannerRetainedStateManifest manifest = capture.Manifest;
        using PlannerRetainedStatePaths paths = PlannerRetainedStatePaths.AdmitRehearsal(
            request.Bundle, request.Output, manifest);
        PlannerRetainedDatabaseInventory baseline = manifest.Databases.Single(database => database.SafetyPath == "safety/planner.db");
        PlannerRetainedRehearsalMap map = BuildMap(paths, manifest);
        ValidateFamilies(request.Bundle, manifest, ct);
        paths.CreateBundle();
        List<PlannerRetainedRehearsalStage> stages = [];
        List<PlannerRetainedRehearsalCheckpoint> checkpoints = [];
        using FilePins pins = new();
        PlannerRetainedRehearsalGuard? guard = null;
        Exception? failure = null;
        string? previousTemp = null;
        string? previousTmp = null;
        bool configuredTemp = false;
        PlannerRetainedRehearsalPreservation preservation = new(
            "verified-at-capture-only", "not-yet-rechecked", "not-yet-rechecked", [], []);
        IReadOnlyList<PlannerRetainedEvidenceFile> files = [];
        try
        {
            PlannerRetainedStateEvidence.WriteJson(paths.Output("rehearsal.started.json"), new
            {
                formatVersion = 1, status = "incomplete", request.CandidateCommit, request.CandidateTree,
                captureManifestSha256 = capture.Completion.ManifestSha256, settings = manifest.Settings, pathMap = map,
            });
            paths.EnsureOutputDirectory(paths.InBundle(map.Temp));
            if (configureProcessTemp)
            {
                previousTemp = Environment.GetEnvironmentVariable("TEMP");
                previousTmp = Environment.GetEnvironmentVariable("TMP");
                Environment.SetEnvironmentVariable("TEMP", paths.InBundle(map.Temp));
                Environment.SetEnvironmentVariable("TMP", paths.InBundle(map.Temp));
                configuredTemp = true;
            }
            Materialize(request.Bundle, paths, manifest, map, pins, ct);
            checkpoint?.Invoke("materialized", paths.InBundle(map.Database));
            Snapshot previous = new(request.Bundle, "capture", baseline);
            for (int pass = 1; pass <= 2 && guard is null; pass++)
            {
                (previous, guard) = await RunPassAsync(pass, previous);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (configuredTemp)
            {
                Environment.SetEnvironmentVariable("TEMP", previousTemp);
                Environment.SetEnvironmentVariable("TMP", previousTmp);
            }
        }

        try
        {
            // Revalidation never follows the original strings in the manifest.
            using PlannerRetainedVerifiedCapture rechecked = await PlannerRetainedStateCapture.OpenVerifiedAsync(
                request.Bundle, request.CandidateCommit, request.CandidateTree, CancellationToken.None);
            preservation = VerifyPreservation(paths, manifest, map);
            files = Catalog(paths);
            foreach (PlannerRetainedEvidenceFile file in files)
            {
                pins.Add(PlannerRetainedStatePaths.OpenFrozenFile(paths.InBundle(file.Path)));
            }
            foreach (PlannerRetainedEvidenceFile file in files)
            {
                if (PlannerRetainedStateEvidence.Fingerprint(paths.Bundle, paths.InBundle(file.Path)) != file)
                {
                    throw Incomplete("completion", "rehearsal-evidence-changed");
                }
            }
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }
        foreach (int pass in new[] { 1, 2 })
        {
            foreach (string name in StageNames)
            {
                if (!stages.Any(stage => stage.Pass == pass && stage.Name == name))
                {
                    stages.Add(new(pass, name, guard is not null && failure is null
                        ? "not-run-after-refusal" : "not-run-after-incomplete", null, null));
                }
            }
        }
        bool complete = failure is null;
        PlannerRetainedRehearsalResult result = new(
            1, complete ? guard is null ? "passed" : "blocked" : "incomplete",
            complete ? "complete" : "incomplete",
            complete ? guard is null ? "passed" : "guarded-refusal" : "not-completed",
            complete ? guard?.Name : null, request.CandidateCommit, request.CandidateTree,
            capture.Completion.ManifestSha256, manifest.Binaries, manifest.Settings, map, baseline,
            stages.OrderBy(stage => stage.Pass).ThenBy(stage => Array.IndexOf(StageNames, stage.Name)).ToArray(),
            checkpoints, guard, preservation, files,
            failure is PlannerRetainedStateException retained ? retained.Category :
                failure is OperationCanceledException ? "cancelled" : failure is null ? null : "unexpected-error",
            failure?.ToString());
        PlannerRetainedStateEvidence.WriteJson(paths.Output("rehearsal.result.json"), result);
        // A successful JSON write is not enough: verify the complete evidence catalog
        // while the bundle lease and output ancestor pins are still held.
        foreach (PlannerRetainedEvidenceFile expected in files)
        {
            if (PlannerRetainedStateEvidence.Fingerprint(paths.Bundle, paths.InBundle(expected.Path)) != expected)
            {
                throw Incomplete("completion", "rehearsal-evidence-changed");
            }
        }
        return result;

        async Task<(Snapshot, PlannerRetainedRehearsalGuard?)> RunPassAsync(int pass, Snapshot previous)
        {
            string databasePath = paths.InBundle(map.Database);
            string stage = "ownership";
            DateTimeOffset started = DateTimeOffset.UtcNow;
            Snapshot before = previous;
            using PlannerDatabase database = new(databasePath, NullLogger<PlannerDatabase>.Instance);
            try
            {
                ct.ThrowIfCancellationRequested();
                ValidateWorkPaths(paths, map, initial: pass == 1);
                checkpoint?.Invoke($"pass{pass}-before-ownership", databasePath);
                database.AcquireStartupOwnership();
                stages.Add(new(pass, stage, "passed", null, null));
                checkpoint?.Invoke($"pass{pass}-ownership-acquired", databasePath);

                stage = "first-open";
                started = DateTimeOffset.UtcNow;
                await ValidateFirstOpenAsync(paths, map, manifest, before, pass, ct);
                checkpoint?.Invoke($"pass{pass}-first-open-validated", databasePath);
                previous = await SaveAsync($"pass{pass}-first-open", before, "equal", started);
                stages.Add(new(pass, stage, "passed", previous.Name, null));

                stage = "initialization";
                before = previous;
                started = DateTimeOffset.UtcNow;
                database.Initialize();
                checkpoint?.Invoke($"pass{pass}-post-initialization", databasePath);
                previous = await SaveAsync($"pass{pass}-post-initialization", before, "initialization", started);
                stages.Add(new(pass, stage, "passed", previous.Name, null));

                stage = "recovery";
                before = previous;
                started = DateTimeOffset.UtcNow;
                await database.RecoverInterruptedMaintenanceLeasesAsync(ct);
                checkpoint?.Invoke($"pass{pass}-post-recovery", databasePath);
                previous = await SaveAsync($"pass{pass}-post-recovery", before, "recovery", started);
                stages.Add(new(pass, stage, "passed", previous.Name, null));

                stage = "cutover";
                before = previous;
                started = DateTimeOffset.UtcNow;
                if (manifest.Settings.ActivateRunBackedAuthoring)
                {
                    await new AuthoringCutoverCoordinator(database.OpenConnection).ActivateAsync(
                        new AuthoringCutoverRequest("jira-fhir", databasePath, paths.InBundle(map.PreCutoverBackup!)),
                        database, ct);
                }
                else
                {
                    await new AuthoringRunStore(database).EnsureProcessorModeAsync("jira-fhir", ct: ct);
                }
                checkpoint?.Invoke($"pass{pass}-post-cutover", databasePath);
                previous = await SaveAsync($"pass{pass}-post-cutover", before, "cutover", started);
                stages.Add(new(pass, stage, "passed", previous.Name, null));

                stage = "reads";
                before = previous;
                started = DateTimeOffset.UtcNow;
                await VerifyReadsAsync(database, before, ct);
                checkpoint?.Invoke($"pass{pass}-post-reads", databasePath);
                previous = await SaveAsync($"pass{pass}-post-reads", before, "equal", started);
                stages.Add(new(pass, stage, "passed", previous.Name, null));
                return (previous, null);
            }
            catch (Exception ex)
            {
                stages.Add(new(pass, stage, "incomplete", null,
                    ex is PlannerRetainedStateException problem ? problem.Category :
                        ex is OperationCanceledException ? "cancelled" : "unexpected-error"));
                // Ownership/materialization failures do not authorize any further
                // SQLite open. Other failures retain the last committed state.
                if (stage == "ownership")
                {
                    throw;
                }
                string name = $"pass{pass}-failure-state";
                Snapshot failed = await TakeSnapshotAsync(paths, map.Database, name, CancellationToken.None);
                PlannerRetainedRehearsalGuard? proven = null;
                Exception? proofFailure = null;
                try
                {
                    proven = await ProveGuardAsync(paths, map, before, failed, stage, ex, started, CancellationToken.None);
                }
                catch (Exception diagnostic)
                {
                    proofFailure = diagnostic;
                }
                string rule = proven is null ? "equal" : proven.Name == "schema-admission-refused" ? "equal" : "guard";
                IReadOnlyList<PlannerRetainedRehearsalDelta> deltas = await ValidateDeltaAsync(
                    paths, before, failed, rule, manifest.Settings.ActivateRunBackedAuthoring,
                    proven?.Name, started, DateTimeOffset.UtcNow, CancellationToken.None);
                PlannerRetainedRehearsalCheckpoint saved = PersistCheckpoint(
                    paths, failed, before, started, deltas);
                checkpoints.Add(saved);
                FreezeCheckpoint(failed.Name);
                stages[^1] = new(pass, stage, proven is not null && saved.Status == "passed"
                    ? "guarded-refusal" : "incomplete", failed.Name, proven?.Name ?? stages[^1].Category);
                if (proofFailure is not null)
                {
                    throw Incomplete("guard-proof", "guard-diagnostic-incomplete", proofFailure);
                }
                if (proven is null || saved.Status != "passed")
                {
                    throw;
                }
                return (failed, proven);
            }
        }

        async Task<Snapshot> SaveAsync(string name, Snapshot before, string rule, DateTimeOffset started)
        {
            Snapshot next = await TakeSnapshotAsync(paths, map.Database, name, ct);
            IReadOnlyList<PlannerRetainedRehearsalDelta> deltas = await ValidateDeltaAsync(
                paths, before, next, rule, manifest.Settings.ActivateRunBackedAuthoring, null,
                started, DateTimeOffset.UtcNow, ct);
            PlannerRetainedRehearsalCheckpoint saved = PersistCheckpoint(paths, next, before, started, deltas);
            checkpoints.Add(saved);
            FreezeCheckpoint(next.Name);
            if (saved.Status != "passed")
            {
                throw Incomplete(name, "unexpected-startup-delta");
            }
            return next;
        }

        void FreezeCheckpoint(string name)
        {
            foreach (PlannerRetainedEvidenceFile file in Catalog(paths, "checkpoints/" + name))
            {
                pins.Add(PlannerRetainedStatePaths.OpenFrozenFile(paths.InBundle(file.Path)));
            }
        }
    }

    private static PlannerRetainedRehearsalMap BuildMap(
        PlannerRetainedStatePaths paths, PlannerRetainedStateManifest manifest)
    {
        string Work(PlannerRetainedPath path) => ("work/" + path.RootId + "/" + path.RelativePath).TrimEnd('/');
        PlannerRetainedRehearsalArtifact[] artifacts = manifest.Artifacts.Select(artifact =>
            new PlannerRetainedRehearsalArtifact(artifact.Id,
                Work(new(artifact.RootId, artifact.RelativePath, true)),
                artifact.Kind == "sqlite-shm" ? "captured-shm-not-materialized" : "independent-exact-copy",
                artifact.Length, artifact.Sha256)).ToArray();
        PlannerRetainedRehearsalMap map = new(
            Work(manifest.SettingsPaths.Database),
            manifest.SettingsPaths.PreCutoverBackup is { } backup ? Work(backup) : null,
            Work(manifest.SettingsPaths.Snapshots), "temp", artifacts);
        foreach (PlannerRetainedRehearsalArtifact artifact in artifacts)
        {
            _ = paths.InBundle(artifact.Path);
        }
        return map;
    }

    private static void ValidateFamilies(string bundle, PlannerRetainedStateManifest manifest, CancellationToken ct)
    {
        foreach (PlannerRetainedArtifact artifact in manifest.Artifacts)
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(artifact.RelativePath);
            using FileStream file = PlannerRetainedStatePaths.OpenFrozenFile(
                PlannerRetainedStatePaths.Under(bundle, artifact.RawPath));
            byte[] header = new byte[100];
            int length = (int)Math.Min(file.Length, header.Length);
            file.ReadExactly(header.AsSpan(0, length));
            bool database = length == 100 && header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8);
            bool journal = name.EndsWith("-journal", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("-mj", StringComparison.OrdinalIgnoreCase) || name.Contains("-sj", StringComparison.OrdinalIgnoreCase) ||
                (length >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }));
            if (journal && file.Length != 0)
            {
                throw Incomplete("materialization", "unsupported-journal");
            }
            if ((artifact.Kind == "database") != database ||
                artifact.Kind is not ("database" or "file" or "sqlite-wal" or "sqlite-shm" or "sqlite-journal" or "empty-super-journal"))
            {
                throw Incomplete("materialization", "invalid-family-map");
            }
            if (artifact.Kind == "sqlite-wal" && file.Length != 0)
            {
                uint pageSize = length >= 32 ? BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)) : 0;
                if (length < 32 || BinaryPrimitives.ReadUInt32BigEndian(header) is not (0x377f0682 or 0x377f0683) ||
                    pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0 ||
                    (file.Length - 32) % (pageSize + 24) != 0)
                {
                    throw Incomplete("materialization", "invalid-family-map");
                }
            }
        }
        List<PlannerRetainedSidecar> expected = [];
        foreach (PlannerRetainedArtifact database in manifest.Artifacts.Where(artifact => artifact.Kind == "database"))
        {
            foreach (string suffix in PlannerRetainedStatePaths.SqliteSuffixes)
            {
                PlannerRetainedArtifact? member = manifest.Artifacts.SingleOrDefault(artifact =>
                    artifact.RootId == database.RootId &&
                    string.Equals(artifact.RelativePath, database.RelativePath + suffix, StringComparison.OrdinalIgnoreCase));
                if (member is not null && member.Kind != "sqlite" + suffix)
                {
                    throw Incomplete("materialization", "invalid-family-map");
                }
                expected.Add(new(database.Id, suffix, member?.Id));
            }
        }
        if (!expected.OrderBy(sidecar => sidecar.DatabaseArtifactId).ThenBy(sidecar => sidecar.Suffix)
                .SequenceEqual(manifest.Sidecars.OrderBy(sidecar => sidecar.DatabaseArtifactId).ThenBy(sidecar => sidecar.Suffix)) ||
            manifest.Artifacts.Any(artifact => artifact.Kind.StartsWith("sqlite-", StringComparison.Ordinal) &&
                !expected.Any(sidecar => sidecar.ArtifactId == artifact.Id)))
        {
            throw Incomplete("materialization", "invalid-family-map");
        }
    }

    private static void Materialize(
        string bundle, PlannerRetainedStatePaths paths, PlannerRetainedStateManifest manifest,
        PlannerRetainedRehearsalMap map, FilePins pins, CancellationToken ct)
    {
        foreach (PlannerRetainedDirectory directory in manifest.Directories)
        {
            paths.EnsureOutputDirectory(paths.InBundle(
                ("work/" + directory.RootId + "/" + directory.RelativePath).TrimEnd('/')));
        }
        foreach (PlannerRetainedRehearsalArtifact mapped in map.Artifacts)
        {
            if (mapped.Disposition == "captured-shm-not-materialized")
            {
                continue;
            }
            PlannerRetainedArtifact input = manifest.Artifacts.Single(artifact => artifact.Id == mapped.ArtifactId);
            Copy(PlannerRetainedStatePaths.Under(bundle, input.RawPath), paths.Output(mapped.Path), ct);
            PlannerRetainedEvidenceFile file = PlannerRetainedStateEvidence.Fingerprint(paths.Bundle, paths.InBundle(mapped.Path), ct);
            if (file.Length != input.Length || file.Sha256 != input.Sha256 ||
                PlannerRetainedStatePaths.InspectFile(paths.InBundle(mapped.Path)) == input.Identity)
            {
                throw Incomplete("materialization", "raw-copy-mismatch");
            }
            if (!IsMutableFamily(map.Database, mapped.Path))
            {
                pins.Add(PlannerRetainedStatePaths.OpenFrozenFile(paths.InBundle(mapped.Path)));
            }
        }
        // The database file is identity-pinned but writable. SQLite may legitimately
        // replace its copy-local sidecars, not the admitted database or its ancestors.
        pins.Add(PlannerRetainedStatePaths.PinFileIdentity(paths.InBundle(map.Database)));
        using (PlannerRetainedStatePaths.CreateFile(paths.Output(map.Database + ".owner.lock")))
        {
        }
        pins.Add(PlannerRetainedStatePaths.PinFileIdentity(paths.InBundle(map.Database + ".owner.lock")));
        if (map.PreCutoverBackup is not null)
        {
            paths.EnsureOutputDirectory(Path.GetDirectoryName(paths.InBundle(map.PreCutoverBackup))!);
        }
        ValidateWorkPaths(paths, map);
    }

    private static bool IsMutableFamily(string database, string path) =>
        path == database || PlannerRetainedStatePaths.SqliteSuffixes.Any(suffix => path == database + suffix);

    private static void ValidateWorkPaths(PlannerRetainedStatePaths paths, PlannerRetainedRehearsalMap map, bool initial = false)
    {
        foreach (string relative in new[] { map.Database, map.Database + ".owner.lock" }
                     .Concat(PlannerRetainedStatePaths.SqliteSuffixes.Select(suffix => map.Database + suffix))
                     .Concat(map.PreCutoverBackup is null ? [] :
                         PlannerRetainedStatePaths.SqliteSuffixes.Prepend("").Select(suffix => map.PreCutoverBackup + suffix))
                     .Concat(map.Artifacts.Where(artifact => artifact.Disposition != "captured-shm-not-materialized")
                         .Select(artifact => artifact.Path)).Distinct(StringComparer.Ordinal))
        {
            string path = paths.InBundle(relative);
            paths.PinAncestors(path);
            if (PlannerRetainedStatePaths.TryAttributes(path, out _))
            {
                _ = PlannerRetainedStatePaths.InspectFile(path);
            }
            else if (map.Artifacts.Any(artifact => artifact.Path == relative &&
                artifact.Disposition != "captured-shm-not-materialized") && !IsMutableFamily(map.Database, relative))
            {
                throw Incomplete("paths", "materialized-member-missing");
            }
        }
        if (initial)
        {
            foreach (PlannerRetainedEvidenceFile file in Catalog(paths, "work"))
            {
                if (file.Path != map.Database + ".owner.lock" && !map.Artifacts.Any(artifact =>
                        artifact.Path == file.Path && artifact.Disposition != "captured-shm-not-materialized"))
                {
                    throw Incomplete("paths", "unexpected-prematerialized-file");
                }
            }
        }
    }

    private static async Task ValidateFirstOpenAsync(
        PlannerRetainedStatePaths paths, PlannerRetainedRehearsalMap map,
        PlannerRetainedStateManifest manifest, Snapshot expected, int pass, CancellationToken ct)
    {
        string database = paths.InBundle(map.Database);
        Dictionary<string, PlannerRetainedEvidenceFile?> family = new(StringComparer.Ordinal);
        foreach (string suffix in new[] { "", "-wal" })
        {
            string file = database + suffix;
            family.Add(suffix, PlannerRetainedStatePaths.TryAttributes(file, out _)
                ? PlannerRetainedStateEvidence.Fingerprint(paths.Bundle, file, ct) : null);
            if (pass == 1)
            {
                PlannerRetainedRehearsalArtifact? raw = map.Artifacts.SingleOrDefault(artifact => artifact.Path == map.Database + suffix);
                if ((raw is null) != (family[suffix] is null) ||
                    (raw is not null && (family[suffix]!.Sha256 != raw.Sha256 || family[suffix]!.Length != raw.Length)))
                {
                    throw Incomplete("first-open", "wal-materialization-divergence");
                }
            }
        }
        if (pass == 1 && PlannerRetainedStatePaths.TryAttributes(database + "-shm", out _))
        {
            throw Incomplete("first-open", "captured-shm-materialized");
        }
        using (SqliteConnection reader = OpenOrdinaryReadOnly(database))
        {
            PlannerRetainedStateInventory.Execute(reader, "BEGIN;");
            try
            {
                string directory = $"checkpoints/pass{pass}-first-open/direct";
                List<(PlannerRetainedRows Expected, string Sql)> queries =
                [
                    (expected.Inventory.Schema, "SELECT type, name, tbl_name, rootpage, sql FROM sqlite_schema ORDER BY type COLLATE BINARY, name COLLATE BINARY"),
                    (expected.Inventory.TableList, "SELECT schema, name, type, ncol, wr, strict FROM pragma_table_list WHERE schema = 'main' ORDER BY name COLLATE BINARY"),
                    (expected.Inventory.IntegrityCheck, "PRAGMA integrity_check;"),
                    (expected.Inventory.ForeignKeyCheck, "PRAGMA foreign_key_check;"),
                ];
                foreach (PlannerRetainedTable table in expected.Inventory.Tables)
                {
                    queries.Add((table.Rows, SelectRows(table)));
                    queries.Add((table.Metadata[0], $"PRAGMA table_xinfo({Quote(table.Name)});"));
                    queries.Add((table.Metadata[1], $"PRAGMA index_list({Quote(table.Name)});"));
                    queries.Add((table.Metadata[2], $"PRAGMA foreign_key_list({Quote(table.Name)});"));
                    List<string> indexes = ReadIndexNames(reader, table.Name);
                    if (indexes.Count != table.Metadata.Count - 3)
                    {
                        throw Incomplete("first-open", "wal-materialization-divergence");
                    }
                    for (int index = 0; index < indexes.Count; index++)
                    {
                        queries.Add((table.Metadata[index + 3], $"PRAGMA index_xinfo({Quote(indexes[index])});"));
                    }
                }
                for (int index = 0; index < queries.Count; index++)
                {
                    (PlannerRetainedRows rows, string sql) = queries[index];
                    PlannerRetainedRows actual = PlannerRetainedStateInventory.WriteRows(
                        reader, paths, $"{directory}/q{index:D6}.rows", sql, ct);
                    if (!SameRows(rows, actual))
                    {
                        throw Incomplete("first-open", "wal-materialization-divergence");
                    }
                }
                // BackupDatabase changes the destination's schema cookie, which is
                // physical metadata, not a row/schema definition. All other header
                // properties, and every typed table value above, must agree.
                using SqliteConnection safety = await OpenSnapshotAsync(expected, ct);
                foreach (string pragma in new[] { "encoding", "user_version", "application_id", "page_size", "auto_vacuum" })
                {
                    if (!Equals(Scalar(reader, $"PRAGMA {pragma}"), Scalar(safety, $"PRAGMA {pragma}")))
                    {
                        throw Incomplete("first-open", "wal-materialization-divergence");
                    }
                }
                _ = PlannerRetainedStateInventory.WriteRows(reader, paths, directory + "/database.rows",
                    "SELECT 'schema_version' AS name, schema_version AS value FROM pragma_schema_version", ct);
            }
            finally
            {
                PlannerRetainedStateInventory.Execute(reader, "ROLLBACK;");
            }
        }
        List<PlannerRetainedEvidenceFile> newEmptySidecars = [];
        foreach ((string suffix, PlannerRetainedEvidenceFile? before) in family)
        {
            string file = database + suffix;
            PlannerRetainedEvidenceFile? after = PlannerRetainedStatePaths.TryAttributes(file, out _)
                ? PlannerRetainedStateEvidence.Fingerprint(paths.Bundle, file, ct) : null;
            // A ReadOnly open of a WAL-mode header with no WAL can create an
            // empty WAL alongside its SHM. There are no captured WAL bytes to
            // regenerate in that case. Record the NEW empty sidecar separately;
            // never excuse removal, addition of frames, or any old-byte change.
            if (suffix == "-wal" && before is null && after is { Length: 0 })
            {
                newEmptySidecars.Add(after);
            }
            else if (before != after)
            {
                throw Incomplete("first-open", "wal-materialization-divergence");
            }
        }
        PlannerRetainedStateEvidence.WriteJson(paths.Output($"checkpoints/pass{pass}-first-open/family.json"), new
        {
            dbAndCapturedWalBytesUnchanged = true, inputs = family, newEmptySidecars, capturedShmMaterialized = false,
            copyLocalShmPresent = PlannerRetainedStatePaths.TryAttributes(database + "-shm", out _),
            connectionMode = "ReadOnly;Pooling=False;query_only=ON;trusted_schema=OFF;nonimmutable",
            manifest.Settings.ActivateRunBackedAuthoring,
        });
    }

    private static async Task<Snapshot> TakeSnapshotAsync(
        PlannerRetainedStatePaths paths, string mappedDatabase, string name, CancellationToken ct)
    {
        string relative = $"checkpoints/{name}/state.db";
        string destination = paths.Output(relative);
        using (PlannerRetainedStatePaths.CreateFile(destination))
        {
        }
        using (SqliteConnection source = OpenOrdinaryReadOnly(paths.InBundle(mappedDatabase)))
        using (SqliteConnection copy = OpenWritableCopy(destination))
        {
            PlannerRetainedStateInventory.Execute(source, "BEGIN;");
            try
            {
                source.BackupDatabase(copy);
            }
            finally
            {
                PlannerRetainedStateInventory.Execute(source, "ROLLBACK;");
            }
        }
        string inventoryDirectory = $"checkpoints/{name}/inventory";
        PlannerRetainedDatabaseInventory inventory;
        try
        {
            inventory = await PlannerRetainedStateInventory.WriteAsync(
                paths, "rehearsal-working-database", relative, inventoryDirectory, false, ct);
        }
        catch (PlannerRetainedStateException ex) when (ex.Stage == "inventory" &&
            ex.Category is "broken-required-relationship" or "wrong-processor-kind")
        {
            // WriteAsync persists the full failed inventory before refusing. Keep
            // that evidence for the delta classifier; never turn failed checks green.
            inventory = PlannerRetainedStateEvidence.ReadJson<PlannerRetainedDatabaseInventory>(
                paths.InBundle(inventoryDirectory + "/inventory.json"));
        }
        return new(paths.Bundle, name, inventory);
    }

    private static PlannerRetainedRehearsalCheckpoint PersistCheckpoint(
        PlannerRetainedStatePaths paths, Snapshot snapshot, Snapshot before, DateTimeOffset started,
        IReadOnlyList<PlannerRetainedRehearsalDelta> deltas)
    {
        PlannerRetainedRehearsalCheckpoint result = new(snapshot.Name,
            deltas.Any(delta => delta.Status == "unexpected") || snapshot.Inventory.Checks.Any(check => check.Status == "failed")
                ? "unexpected" : "passed",
            started, DateTimeOffset.UtcNow, before.Name, snapshot.Inventory, deltas,
            $"checkpoints/{snapshot.Name}/deltas.ndjson");
        PlannerRetainedStateEvidence.WriteJson(paths.Output($"checkpoints/{snapshot.Name}/checkpoint.json"), result);
        return result;
    }

    private static async Task VerifyReadsAsync(PlannerDatabase database, Snapshot snapshot, CancellationToken ct)
    {
        using SqliteConnection source = await OpenSnapshotAsync(snapshot, ct);
        using (SqliteCommand command = source.CreateCommand())
        {
            command.CommandText = "SELECT Key FROM planned_tickets ORDER BY Key";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string key = reader.GetString(0);
                if (await database.GetPlannedTicketAsync(key, ct) is null)
                {
                    throw Incomplete("reads", "inventoried-plan-not-readable");
                }
                bool hydrated = new[]
                {
                    "planned_ticket_hydration", "planned_jira_hydration", "planned_zulip_hydration",
                    "planned_github_hydration", "planned_repo_hydration", "planned_ticket_jira_xref",
                }.Any(table => Count(source, $"SELECT COUNT(*) FROM {Quote(table)} WHERE IssueKey = @key", ("@key", key)) != 0);
                if ((await database.GetHydrationAsync(key, ct) is not null) != hydrated)
                {
                    throw Incomplete("reads", "inventoried-hydration-not-readable");
                }
            }
        }
        using (SqliteCommand command = source.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT WorkGroupClean, Specification, Type FROM planned_ticket_topics";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (await database.GetWorkGroupTopicsAsync(reader.GetString(0), reader.GetString(1), reader.GetString(2), ct) is null)
                {
                    throw Incomplete("reads", "inventoried-grouping-not-readable");
                }
                _ = await database.GetClusteringSignalsAsync(reader.GetString(0), ct);
            }
        }
    }

    private static PlannerRetainedRehearsalPreservation VerifyPreservation(
        PlannerRetainedStatePaths paths, PlannerRetainedStateManifest manifest, PlannerRetainedRehearsalMap map)
    {
        List<PlannerRetainedRehearsalArtifact> artifacts = [];
        foreach (PlannerRetainedRehearsalArtifact artifact in map.Artifacts)
        {
            string disposition = artifact.Disposition;
            if (disposition == "captured-shm-not-materialized")
            {
                artifacts.Add(artifact);
                continue;
            }
            if (IsMutableFamily(map.Database, artifact.Path))
            {
                artifacts.Add(artifact with { Disposition = "mutable-working-family;raw-preserved-in-bundle" });
                continue;
            }
            PlannerRetainedEvidenceFile actual = PlannerRetainedStateEvidence.Fingerprint(paths.Bundle, paths.InBundle(artifact.Path));
            if (actual.Length != artifact.Length || actual.Sha256 != artifact.Sha256)
            {
                throw Incomplete("preservation", "copied-artifact-changed");
            }
            artifacts.Add(artifact with { Disposition = "byte-identical" });
        }
        IReadOnlyList<PlannerRetainedEvidenceFile> current = Catalog(paths, "work");
        List<PlannerRetainedEvidenceFile> added = current.Where(file =>
            !map.Artifacts.Any(artifact => artifact.Path == file.Path && artifact.Disposition != "captured-shm-not-materialized")).ToList();
        foreach (PlannerRetainedEvidenceFile file in added)
        {
            bool simulatedBackup = map.PreCutoverBackup is not null && !manifest.SettingsPaths.PreCutoverBackup!.Existed &&
                (file.Path == map.PreCutoverBackup || PlannerRetainedStatePaths.SqliteSuffixes.Any(suffix =>
                    file.Path == map.PreCutoverBackup + suffix));
            bool sidecar = file.Path == map.Database + ".owner.lock" || manifest.Artifacts
                .Where(artifact => artifact.Kind == "database")
                .Any(artifact => PlannerRetainedStatePaths.SqliteSuffixes.Any(suffix =>
                    file.Path == $"work/{artifact.RootId}/{artifact.RelativePath}{suffix}"));
            if (!simulatedBackup && !sidecar)
            {
                throw Incomplete("preservation", "unexpected-work-artifact");
            }
        }
        return new("verified-at-capture-only;not-reopened", "byte-identical-reverified-lease",
            "byte-identical-except-declared-mutable-family-and-regenerated-shm", artifacts, added);
    }

    private static IReadOnlyList<PlannerRetainedEvidenceFile> Catalog(PlannerRetainedStatePaths paths, string? relative = null)
    {
        List<PlannerRetainedEvidenceFile> files = [];
        Walk(relative is null ? paths.Bundle : paths.InBundle(relative));
        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();

        void Walk(string directory)
        {
            using var pin = PlannerRetainedStatePaths.FreezeDirectory(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                if (!PlannerRetainedStatePaths.TryAttributes(path, out FileAttributes attributes) ||
                    attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw Incomplete("preservation", "output-membership-changed");
                }
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    Walk(path);
                }
                else
                {
                    files.Add(PlannerRetainedStateEvidence.Fingerprint(paths.Bundle, path));
                }
            }
        }
    }

    private static void Copy(string input, string output, CancellationToken ct)
    {
        using FileStream source = PlannerRetainedStatePaths.OpenFrozenFile(input);
        using FileStream destination = PlannerRetainedStatePaths.CreateFile(output);
        byte[] buffer = new byte[81920];
        int length;
        while ((length = source.Read(buffer)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, length);
        }
        destination.Flush(flushToDisk: true);
    }

    private static SqliteConnection OpenOrdinaryReadOnly(string path)
    {
        PlannerRetainedStatePaths.CheckAncestors(path);
        _ = PlannerRetainedStatePaths.InspectFile(path);
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            PlannerRetainedStateInventory.Execute(connection, "PRAGMA query_only = ON; PRAGMA trusted_schema = OFF;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnection OpenWritableCopy(string path)
    {
        PlannerRetainedStatePaths.CheckAncestors(path);
        _ = PlannerRetainedStatePaths.InspectFile(path);
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static Task<SqliteConnection> OpenSnapshotAsync(Snapshot snapshot, CancellationToken ct)
    {
        PlannerRetainedEvidenceFile file = PlannerRetainedStateEvidence.Fingerprint(snapshot.Root, snapshot.Database, ct);
        if (file.Length != snapshot.Inventory.SafetyLength || file.Sha256 != snapshot.Inventory.SafetySha256 ||
            PlannerRetainedStatePaths.SqliteSuffixes.Any(suffix => PlannerRetainedStatePaths.TryAttributes(snapshot.Database + suffix, out _)))
        {
            throw Incomplete("classifier", "checkpoint-not-frozen-standalone");
        }
        return SqliteReviewSnapshotValidator.OpenReadOnlyAsync(snapshot.Database, ct);
    }

    private static string Quote(string identifier) => PlannerRetainedStateInventory.Quote(identifier);
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static bool SameRows(PlannerRetainedRows left, PlannerRetainedRows right) =>
        left.RowCount == right.RowCount && left.Sha256 == right.Sha256 && left.Columns.SequenceEqual(right.Columns);
    private static string SelectRows(PlannerRetainedTable table, IEnumerable<string>? expressions = null, string? where = null) =>
        $"SELECT {string.Join(", ", expressions ?? table.Rows.Columns.Select(Quote))} FROM {Quote(table.Name)}" +
        (where is null ? "" : " WHERE " + where) + " ORDER BY " +
        (table.RowIdExpression is not null ? Quote(table.RowIdExpression) : string.Join(", ",
            table.Columns.Where(column => column.PrimaryKeyOrder != 0).OrderBy(column => column.PrimaryKeyOrder)
                .Select(column => Quote(column.Name))));

    private static List<string> ReadIndexNames(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list({Quote(table)});";
        List<string> names = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }
        return names.Order(StringComparer.Ordinal).ToList();
    }

    private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] values)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in values)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command.ExecuteScalar();
    }

    private static long Count(SqliteConnection connection, string sql, params (string Name, object? Value)[] values) =>
        Convert.ToInt64(Scalar(connection, sql, values), CultureInfo.InvariantCulture);

    private static readonly IReadOnlyDictionary<string, (string Column, string Definition)[]> Additions =
        new Dictionary<string, (string, string)[]>(StringComparer.Ordinal)
        {
            [SourceTable] =
            [
                ("Specification", "TEXT NOT NULL DEFAULT ''"), ("SourceProjectLastSuccessfulRefreshAt", "TEXT NULL"),
                ("SourceContentRevision", "INTEGER NULL"), ("CompletionId", "TEXT NULL"),
            ],
            [RunsTable] = [("Purpose", "TEXT NOT NULL DEFAULT 'authoring'"), ("SourceRunId", "TEXT NULL"), ("RequestJson", "TEXT NULL")],
            [ItemsTable] = [("PostPersistenceLeaseId", "TEXT NULL"), ("PostPersistenceLeaseAcquiredAt", "TEXT NULL")],
            ["authoring_review_snapshots"] = [("PublicationProofJson", "TEXT NULL")],
            [StateTable] = [("ReceiptContentHash", "TEXT NULL")],
            ["planned_ticket_hydration"] = HydrationAdditions(),
            ["planned_jira_hydration"] = HydrationAdditions(),
        };

    private static (string, string)[] HydrationAdditions() =>
        new[] { "DescriptionHtml", "ResolutionDescriptionHtml", "Reporter", "CreatedAt", "RelatedArtifactsRaw", "RelatedPagesRaw" }
            .Select(column => (column, "TEXT NULL")).ToArray();

    private sealed record SchemaObject(long RowId, string Type, string Name, string Table, long RootPage, string? Sql);
    private sealed class SqliteIdentifierComparer : IEqualityComparer<string>
    {
        internal static readonly SqliteIdentifierComparer Instance = new();
        public bool Equals(string? left, string? right) => string.Equals(Fold(left), Fold(right), StringComparison.Ordinal);
        public int GetHashCode(string value) => StringComparer.Ordinal.GetHashCode(Fold(value)!);
        private static string? Fold(string? value) => value is null ? null : string.Concat(value.Select(character =>
            character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character));
    }
    private static readonly IReadOnlyDictionary<string, SchemaObject> SchemaContract = BuildSchemaContract();

    // This is a declarative catalog of the pinned owner/store DDL, NOT a donor
    // produced by running EnsureSchema. Bare fields are TEXT NOT NULL, ? is
    // nullable, :i is INTEGER, ! is UNIQUE, # is the generated rowid alias.
    private static IReadOnlyDictionary<string, SchemaObject> BuildSchemaContract()
    {
        Dictionary<string, SchemaObject> result = new(SqliteIdentifierComparer.Instance);
        Record(SourceTable, "RowId# Id! Key Title Description? Project Status WorkGroup Type Specification SourceTicketShape LastSyncedAt LastUpdated? SourceProjectLastSuccessfulRefreshAt? SourceContentRevision:i? StartedProcessingAt? CompletedProcessingAt? LastProcessingAttemptAt? ProcessingStatus? ProcessingError? ProcessingAttemptCount:i CompletionId? ErrorMessage? AgentExitCode:i? ErrorOccurredAt?",
            "Key Project Status WorkGroup Type Specification SourceTicketShape LastUpdated ProcessingStatus StartedProcessingAt CompletedProcessingAt LastProcessingAttemptAt CompletionId");
        Record(RunsTable, "RowId# Id! ProcessorKind AuthoringEpoch:i Status Purpose=authoring SourceRunId? DatabaseOnly:i TotalItems:i CreatedAt StartedAt? CompletedAt? Error? SnapshotId? RequestJson?", "ProcessorKind Status Purpose SourceRunId");
        Record(ItemsTable, "RowId# Id! RunId BusinessKey ItemKind ExpectedSourceRevision Status CurrentOperationId? AcceptedReceiptId? PostPersistenceLeaseId? PostPersistenceLeaseAcquiredAt? AttemptCount:i CreatedAt StartedAt? CompletedAt? Error?", "RunId BusinessKey Status CurrentOperationId AcceptedReceiptId");
        Record("authoring_run_input_provenance", "RowId# RunId Source LatestSuccessfulRefreshAt? ContentRevision:i? CapturedAt", "RunId Source");
        Record("authoring_run_attempts", "RowId# OperationId! RunId RunItemId AttemptNumber:i TokenVerifier Status ContentHash? ObservedSourceRevision? CreatedAt CompletedAt? Error?", "RunId RunItemId Status");
        Record("authoring_run_stages", "RowId# Id! RunId StageName PartitionKey InputFingerprint Status AttemptCount:i LeaseId? LeaseAcquiredAt? CreatedAt StartedAt? CompletedAt? Error?", "RunId Status");
        Record("authoring_result_receipts", "RowId# Id! OperationId! RunId RunItemId BusinessKey ContentHash ExpectedSourceRevision ObservedSourceRevision AuthoringEpoch:i PersistedAt", "RunId RunItemId BusinessKey");
        Record(FencesTable, "RowId# ProcessorKind! RunId LeaseId AcquiredAt");
        Record(ModeTable, "RowId# ProcessorKind! Mode Epoch:i RevalidationRequired:i RevalidationRunId? UpdatedAt");
        Record("authoring_review_snapshots", "RowId# Id! ProcessorKind RunId AuthoringEpoch:i Sequence:i SchemaVersion:i Status TempPath Path ChecksumSha256? SizeBytes:i ItemCount:i ReceiptCount:i TableCountsJson PublicationProofJson? CreatedAt FinalizedAt? Error?", "ProcessorKind RunId Status Sequence");
        Table("authoring_revalidation_lineage", "RunId TEXT NOT NULL PRIMARY KEY, PreviousRunId TEXT NOT NULL UNIQUE");
        Record("planned_tickets", "RowId# Id! Key! Resolution ResolutionSummary FeatureProposal DesignRationale SavedAt");
        Record("planned_ticket_jira_content", "RowId# TicketKey! DescriptionHtml? ResolutionDescriptionHtml?");
        Record("planned_ticket_repos", "RowId# Id! IssueKey RepoKey RepoRevision? Justification", "IssueKey RepoKey");
        Record("planned_ticket_repo_changes", "RowId# Id! IssueKey TicketRepoId RepoKey ChangeSequence:i FilePath ChangeTitle ChangeDescription SourceLineStart:i? SourceLineEnd:i? ReplacementLines Reason", "IssueKey TicketRepoId RepoKey FilePath");
        Record("planned_ticket_repo_impacts", "RowId# Id! IssueKey TicketRepoId RepoKey TicketRepoChangeId? AffectedFilePath HowAffected", "IssueKey TicketRepoId RepoKey TicketRepoChangeId");
        Record("planned_ticket_change_validations", "RowId# Id! IssueKey TicketRepoId RepoKey ValidationSequence:i Action", "IssueKey TicketRepoId RepoKey");
        Record("planned_ticket_testing_considerations", "RowId# Id! IssueKey TicketRepoId RepoKey ConsiderationSequence:i Consideration", "IssueKey TicketRepoId RepoKey");
        Record("planned_ticket_open_questions", "RowId# Id! IssueKey TicketRepoId RepoKey QuestionSequence:i Question", "IssueKey TicketRepoId RepoKey");
        Record("planned_ticket_related_jira", "RowId# IssueKey JiraKey Source", "IssueKey");
        Record("planned_ticket_related_zulip", "RowId# IssueKey ZulipThreadId", "IssueKey");
        Record("planned_ticket_related_github", "RowId# IssueKey GitHubItemId", "IssueKey");
        Record("planned_ticket_jira_xref", "RowId# IssueKey JiraKey Source", "IssueKey JiraKey");
        Record("planned_ticket_hydration", "RowId# IssueKey! Priority? Resolution? ResolutionDescriptionPlain? Specification? RaisedInVersion? SelectedBallot? ChangeCategory? Impact? Labels? CommentCount:i? DescriptionPlain? DescriptionHtml? ResolutionDescriptionHtml? Reporter? CreatedAt? RelatedArtifactsRaw? RelatedPagesRaw? HydratedAt HydrationStatus HydrationReason?", "IssueKey");
        Record("planned_jira_hydration", "RowId# IssueKey JiraKey Title? Status? Type? Priority? Resolution? ResolutionDescriptionPlain? WorkGroup? WorkGroupClean? Specification? UpdatedAt? Url? DescriptionHtml? ResolutionDescriptionHtml? Reporter? CreatedAt? RelatedArtifactsRaw? RelatedPagesRaw? HydratedAt HydrationStatus HydrationReason?", "IssueKey JiraKey WorkGroupClean");
        Record("planned_zulip_hydration", "RowId# IssueKey ZulipThreadId StreamId:i? StreamName? Topic? MessageCount:i? FirstMessageAt? LastMessageAt? FirstMessageExcerpt? Url? HydratedAt HydrationStatus HydrationReason?", "IssueKey");
        Record("planned_github_hydration", "RowId# IssueKey GitHubItemId Owner? Repo? Number:i? Path? Title? State? IsPullRequest:i? Labels? UpdatedAt? Url? HydratedAt HydrationStatus HydrationReason?", "IssueKey");
        Record("planned_repo_hydration", "RowId# IssueKey RepoKey Description? WorkGroup? Specification? CategoryDetail? Url? HydratedAt HydrationStatus HydrationReason?", "IssueKey RepoKey");
        Record("planned_ticket_topics", "RowId# Id! WorkGroupClean WorkGroupDisplay Specification Type ShortDescription LongerDescription RenderOrderHint:i? SavedAt", "WorkGroupClean Specification Type");
        Record("planned_ticket_topic_groups", "RowId# Id! TopicRowId:i FirstTicketKey Rationale OrderInTopic:i SavedAt", "TopicRowId FirstTicketKey");
        Record("planned_ticket_topic_members", "RowId# Id! TopicRowId:i TopicGroupRowId:i? TicketKey OrderInContainer:i", "TopicRowId TopicGroupRowId TicketKey");
        Record("planned_ticket_topic_repos", "RowId# Id! TopicRowId:i RepoKey OrderInTopic:i", "TopicRowId RepoKey");
        Record("jira_review_workgroups", "RowId# Code! Name NameClean UpdatedAt");
        Table("planner_schema_migrations", "Id TEXT PRIMARY KEY, AppliedAt TEXT NOT NULL");
        Table(StateTable, "TicketKey TEXT PRIMARY KEY, Classification TEXT NOT NULL, GraphHash TEXT NOT NULL, ReceiptContentHash TEXT NULL, LegacyCompletionId TEXT NULL, LegacyCompletedProcessingAt TEXT NULL, RunId TEXT NULL, RunItemId TEXT NULL, OperationId TEXT NULL, UpdatedAt TEXT NOT NULL");
        Table("planned_ticket_partition_receipts", "RunId TEXT NOT NULL, StageId TEXT NOT NULL, PartitionKey TEXT NOT NULL, InputFingerprint TEXT NOT NULL, TopicRows INTEGER NOT NULL, TopicGroupRows INTEGER NOT NULL, MemberRows INTEGER NOT NULL, PersistedAt TEXT NOT NULL, PRIMARY KEY(RunId, PartitionKey)");
        Table("planned_ticket_run_item_partitions", "RunItemId TEXT PRIMARY KEY, TicketKey TEXT NOT NULL, WorkGroupClean TEXT NULL, WorkGroupDisplay TEXT NULL, Specification TEXT NULL, Type TEXT NULL, CapturedAt TEXT NOT NULL");
        Table("planned_ticket_applier_projection_pending", "OperationId TEXT PRIMARY KEY, TicketKey TEXT NOT NULL, PreserveLegacy INTEGER NOT NULL");
        Index("idx_jira_processing_source_tickets_key_shape", SourceTable, "Key COLLATE NOCASE, SourceTicketShape COLLATE NOCASE", true);
        Index("idx_authoring_run_items_identity", ItemsTable, "RunId, ItemKind COLLATE NOCASE, BusinessKey COLLATE NOCASE", true);
        Index("idx_authoring_run_items_revision", ItemsTable, "ItemKind COLLATE NOCASE, BusinessKey COLLATE NOCASE, ExpectedSourceRevision", true, " WHERE Status <> 'superseded'");
        Index("idx_authoring_run_items_lifecycle", ItemsTable, "RunId, Status, CompletedAt, RowId");
        Index("idx_authoring_run_input_provenance_identity", "authoring_run_input_provenance", "RunId, Source COLLATE NOCASE", true);
        Index("idx_authoring_run_attempts_number", "authoring_run_attempts", "RunItemId, AttemptNumber", true);
        Index("idx_authoring_run_stages_identity", "authoring_run_stages", "RunId, StageName, PartitionKey", true);
        // The generated IDX_authoring_review_snapshots_Sequence is created first.
        // SQLite folds identifier case, so the owner's later IF NOT EXISTS with
        // the same name does not replace it with (ProcessorKind, Sequence).
        // Preserve that actual order, rather than inventing a new uniqueness rule.
        Index("idx_planned_ticket_authoring_state_classification", StateTable, "Classification");
        foreach ((string name, string table, string terms) in new[]
        {
            ("idx_planned_ticket_topic_repos_topic_repo", "planned_ticket_topic_repos", "TopicRowId, RepoKey"),
            ("idx_planned_jira_hydration_issue_jira", "planned_jira_hydration", "IssueKey, JiraKey"),
            ("idx_planned_zulip_hydration_issue_thread", "planned_zulip_hydration", "IssueKey, ZulipThreadId"),
            ("idx_planned_github_hydration_issue_item", "planned_github_hydration", "IssueKey, GitHubItemId"),
            ("idx_planned_repo_hydration_issue_repo", "planned_repo_hydration", "IssueKey, RepoKey"),
            ("idx_planned_ticket_related_jira_issue_jira", "planned_ticket_related_jira", "IssueKey, JiraKey"),
            ("idx_planned_ticket_related_zulip_issue_thread", "planned_ticket_related_zulip", "IssueKey, ZulipThreadId"),
            ("idx_planned_ticket_related_github_issue_item", "planned_ticket_related_github", "IssueKey, GitHubItemId"),
            ("idx_planned_ticket_jira_xref_issue_jira", "planned_ticket_jira_xref", "IssueKey, JiraKey"),
        })
        {
            Index(name, table, terms, true);
        }
        const string trigger = "trg_planned_ticket_receipt_applier_projection";
        result.Add(trigger, new(0, "trigger", trigger, "authoring_result_receipts", 0, """
            CREATE TRIGGER trg_planned_ticket_receipt_applier_projection
            AFTER INSERT ON authoring_result_receipts
            WHEN EXISTS(SELECT 1 FROM planned_ticket_applier_projection_pending WHERE OperationId = NEW.OperationId)
            BEGIN
                UPDATE jira_processing_source_tickets
                SET ProcessingStatus = 'complete', ProcessingError = NULL, ErrorMessage = NULL,
                    AgentExitCode = NULL, ErrorOccurredAt = NULL,
                    CompletionId = CASE
                        WHEN (SELECT PreserveLegacy FROM planned_ticket_applier_projection_pending
                              WHERE OperationId = NEW.OperationId) = 1 THEN CompletionId ELSE NEW.Id END,
                    CompletedProcessingAt = CASE
                        WHEN (SELECT PreserveLegacy FROM planned_ticket_applier_projection_pending
                              WHERE OperationId = NEW.OperationId) = 1 THEN CompletedProcessingAt ELSE NEW.PersistedAt END
                WHERE Key = (SELECT TicketKey FROM planned_ticket_applier_projection_pending WHERE OperationId = NEW.OperationId);
                DELETE FROM planned_ticket_applier_projection_pending WHERE OperationId = NEW.OperationId;
            END
            """));
        return result;

        void Record(string name, string fields, string indexes = "")
        {
            List<string> columns = [];
            foreach (string field in fields.Split(' '))
            {
                string column = field.TrimEnd('?', '!', '#').Split(':', '=')[0];
                string definition = field.EndsWith('#') ? "INTEGER UNIQUE PRIMARY KEY NOT NULL" :
                    (field.Contains(":i", StringComparison.Ordinal) ? "INTEGER" : "TEXT") +
                    (field.EndsWith('!') ? " UNIQUE" : "") + (field.EndsWith('?') ? "" : " NOT NULL") +
                    (field.Contains('=') ? " DEFAULT " + Literal(field.Split('=')[1]) : "");
                columns.Add(column + " " + definition);
            }
            Table(name, string.Join(", ", columns));
            foreach (string column in indexes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                Index("IDX_" + name + "_" + column, name, Quote(column));
            }
        }

        void Table(string name, string columns)
        {
            result.Add(name, new(0, "table", name, name, 0, $"CREATE TABLE {name} ({columns})"));
            int index = 0;
            foreach (string segment in DefinitionSegments(columns))
            {
                string normalized = NormalizeSql(segment);
                if (normalized.Contains(" unique", StringComparison.Ordinal) ||
                    (normalized.Contains("primary key", StringComparison.Ordinal) &&
                        !normalized.Contains("integer unique primary key", StringComparison.Ordinal)))
                {
                    string implicitName = $"sqlite_autoindex_{name}_{++index}";
                    result.Add(implicitName, new(0, "index", implicitName, name, 0, null));
                }
            }
        }

        void Index(string name, string table, string terms, bool unique = false, string predicate = "") =>
            result.Add(name, new(0, "index", name, table, 0,
                $"CREATE {(unique ? "UNIQUE " : "")}INDEX {Quote(name)} ON {Quote(table)} ({terms}){predicate}"));
    }

    private static string NormalizeSql(string sql)
    {
        string[] tokens = Regex.Matches(sql,
            "'(?:''|[^'])*'|\"(?:\"\"|[^\"])*\"|\\[(?:\\]\\]|[^\\]])*\\]|`(?:``|[^`])*`|[\\p{L}\\p{N}_]+|<>|!=|<=|>=|\\S")
            .Select(match => match.Value).Select(token =>
            {
                if (token.StartsWith('\''))
                {
                    return token;
                }
                if (token.StartsWith('"') || token.StartsWith('[') || token.StartsWith('`'))
                {
                    token = token[1..^1].Replace("\"\"", "\"").Replace("]]", "]").Replace("``", "`");
                }
                return string.Concat(token.Select(character => character is >= 'A' and <= 'Z'
                    ? (char)(character + ('a' - 'A')) : character));
            }).ToArray();
        return string.Join(' ', tokens).Replace(" if not exists ", " ", StringComparison.Ordinal).TrimEnd(' ', ';');
    }

    private static IEnumerable<string> DefinitionSegments(string columns)
    {
        int depth = 0;
        int start = 0;
        char quote = '\0';
        for (int index = 0; index < columns.Length; index++)
        {
            char value = columns[index];
            if (quote != '\0')
            {
                if (value == quote)
                {
                    if (index + 1 < columns.Length && columns[index + 1] == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                continue;
            }
            if (value is '\'' or '"' or '`' or '[')
            {
                quote = value == '[' ? ']' : value;
            }
            else if (value == '(') depth++;
            else if (value == ')') depth--;
            else if (value == ',' && depth == 0)
            {
                yield return columns[start..index].Trim();
                start = index + 1;
            }
        }
        yield return columns[start..].Trim();
    }

    private static Dictionary<string, SchemaObject> ReadSchema(SqliteConnection connection)
    {
        Dictionary<string, SchemaObject> objects = new(SqliteIdentifierComparer.Instance);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT rowid, type, name, tbl_name, rootpage, sql FROM sqlite_schema";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            SchemaObject value = new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5));
            objects.Add(value.Name, value);
        }
        return objects;
    }

    private static string? ValidateSchemaDelta(SqliteConnection before, SqliteConnection after, bool initialization)
    {
        Dictionary<string, SchemaObject> old = ReadSchema(before);
        Dictionary<string, SchemaObject> current = ReadSchema(after);
        foreach ((string name, SchemaObject value) in old)
        {
            if (!current.TryGetValue(name, out SchemaObject? actual) || actual.Type != value.Type || actual.Table != value.Table)
            {
                return "missing-or-retyped-object:" + name;
            }
            bool replacement = initialization && name is "idx_authoring_run_items_identity" or "idx_authoring_run_items_revision";
            if (initialization && name == "idx_jira_processing_source_tickets_key_shape" && value.Sql != actual.Sql)
            {
                replacement = IsLegacySourceIndex(before);
            }
            if (replacement)
            {
                if (actual.Sql is null || NormalizeSql(actual.Sql) != NormalizeSql(SchemaContract[name].Sql!))
                {
                    return "replacement-definition:" + name;
                }
                continue;
            }
            if (actual.RowId != value.RowId || actual.RootPage != value.RootPage)
            {
                return "old-schema-row-identity-or-rootpage:" + name;
            }
            if (actual.Sql != value.Sql && !(initialization && value.Type == "table" &&
                ValidAddedTableSql(value, actual, before, after)))
            {
                return "old-object-definition:" + name;
            }
            if (value.Type == "index" && !SameQuery(before, after, $"PRAGMA index_xinfo({Quote(name)});"))
            {
                return "old-index-metadata:" + name;
            }
        }
        foreach ((string name, SchemaObject value) in current.Where(pair => !old.ContainsKey(pair.Key)))
        {
            if (!initialization || !SchemaContract.TryGetValue(name, out SchemaObject? expected) ||
                value.Type != expected.Type || value.Table != expected.Table ||
                (value.Sql is null ? expected.Sql is not null || old.ContainsKey(value.Table) :
                    expected.Sql is null || NormalizeSql(value.Sql) != NormalizeSql(expected.Sql)))
            {
                return "unexpected-added-object:" + name;
            }
        }
        if (initialization && SchemaContract.Values.Any(expected =>
                (expected.Sql is not null || !old.ContainsKey(expected.Table)) && !current.ContainsKey(expected.Name)))
        {
            return "missing-declared-object:" + SchemaContract.Values.First(expected =>
                (expected.Sql is not null || !old.ContainsKey(expected.Table)) && !current.ContainsKey(expected.Name)).Name;
        }
        foreach (string table in old.Values.Where(value => value.Type == "table").Select(value => value.Name))
        {
            if (!SameQuery(before, after, $"PRAGMA foreign_key_list({Quote(table)});"))
            {
                return "old-foreign-key:" + table;
            }
            List<PlannerRetainedColumn> originalColumns = PlannerRetainedStateInventory.ReadColumns(before, table);
            List<PlannerRetainedColumn> currentColumns = PlannerRetainedStateInventory.ReadColumns(after, table);
            if (!currentColumns.Take(originalColumns.Count).SequenceEqual(originalColumns))
            {
                return "old-column:" + table;
            }
            (string Column, string Definition)[] additions = initialization && Additions.TryGetValue(table, out var declared)
                ? declared.Where(addition => !originalColumns.Any(column => column.Name == addition.Column)).ToArray() : [];
            if (currentColumns.Count != originalColumns.Count + additions.Length)
            {
                return "column-count:" + table;
            }
            for (int index = 0; index < additions.Length; index++)
            {
                (string column, string definition) = additions[index];
                PlannerRetainedColumn expected = new(originalColumns.Count + index, column,
                    definition.StartsWith("INTEGER", StringComparison.Ordinal) ? "INTEGER" : "TEXT",
                    definition.Contains("NOT NULL", StringComparison.Ordinal),
                    definition.Contains("DEFAULT", StringComparison.Ordinal) ? definition.Split("DEFAULT ")[1] : null, 0, 0);
                if (currentColumns[originalColumns.Count + index] != expected)
                {
                    return "added-column-definition:" + table + "." + column;
                }
            }
        }
        foreach (string pragma in new[] { "encoding", "user_version", "application_id", "page_size", "auto_vacuum" })
        {
            if (!Equals(Scalar(before, $"PRAGMA {pragma}"), Scalar(after, $"PRAGMA {pragma}")))
            {
                return "database-header:" + pragma;
            }
        }
        return null;
    }

    private static bool ValidAddedTableSql(SchemaObject before, SchemaObject after, SqliteConnection source, SqliteConnection target)
    {
        if (!Additions.TryGetValue(before.Name, out var declared) || before.Sql is null || after.Sql is null)
        {
            return false;
        }
        HashSet<string> columns = PlannerRetainedStateInventory.ReadColumns(source, before.Name).Select(column => column.Name).ToHashSet();
        string oldSql = before.Sql;
        string newSql = after.Sql;
        int opening = newSql.IndexOf('(');
        int closing = newSql.LastIndexOf(')');
        if (opening < 0 || closing < opening)
        {
            return false;
        }
        List<string> segments = DefinitionSegments(newSql[(opening + 1)..closing]).ToList();
        foreach ((string column, string definition) in declared.Where(addition => !columns.Contains(addition.Column)))
        {
            int index = segments.FindIndex(segment => NormalizeSql(segment) == NormalizeSql(column + " " + definition));
            if (index < 0)
            {
                return false;
            }
            segments.RemoveAt(index);
        }
        string stripped = newSql[..(opening + 1)] + string.Join(", ", segments) + newSql[closing..];
        return NormalizeSql(stripped) == NormalizeSql(oldSql);
    }

    private static bool IsLegacySourceIndex(SqliteConnection connection)
    {
        string name = "idx_jira_processing_source_tickets_key_shape";
        if (Count(connection, "SELECT COUNT(*) FROM pragma_index_list(@table) WHERE name = @name AND \"unique\" = 1 AND partial = 0",
                ("@table", SourceTable), ("@name", name)) != 1)
        {
            return false;
        }
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name, coll, \"desc\" FROM pragma_index_xinfo(@name) WHERE key = 1 ORDER BY seqno";
        command.Parameters.AddWithValue("@name", name);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() && reader.GetString(0) == "Key" && reader.GetString(1) == "BINARY" && reader.GetInt64(2) == 0 &&
            reader.Read() && reader.GetString(0) == "SourceTicketShape" && reader.GetString(1) == "BINARY" && reader.GetInt64(2) == 0 &&
            !reader.Read();
    }

    private static bool SameQuery(SqliteConnection left, SqliteConnection right, string sql)
    {
        using SqliteCommand first = left.CreateCommand();
        using SqliteCommand second = right.CreateCommand();
        first.CommandText = second.CommandText = sql;
        using SqliteDataReader a = first.ExecuteReader();
        using SqliteDataReader b = second.ExecuteReader();
        while (a.Read())
        {
            if (!b.Read() || a.FieldCount != b.FieldCount)
            {
                return false;
            }
            for (int column = 0; column < a.FieldCount; column++)
            {
                if (!Equals(a.GetValue(column), b.GetValue(column)))
                {
                    return false;
                }
            }
        }
        return !b.Read();
    }

    private static async Task<IReadOnlyList<PlannerRetainedRehearsalDelta>> ValidateDeltaAsync(
        PlannerRetainedStatePaths paths, Snapshot before, Snapshot after, string boundary,
        bool activation, string? guard, DateTimeOffset started, DateTimeOffset finished, CancellationToken ct)
    {
        using SqliteConnection source = await OpenSnapshotAsync(before, ct);
        using SqliteConnection target = await OpenSnapshotAsync(after, ct);
        List<PlannerRetainedRehearsalDelta> deltas = [];
        using FileStream detailFile = PlannerRetainedStatePaths.CreateFile(
            paths.Output($"checkpoints/{after.Name}/deltas.ndjson"));
        using StreamWriter details = new(detailFile, new UTF8Encoding(false), 81920, leaveOpen: true);
        string? schemaProblem = ValidateSchemaDelta(source, target, boundary == "initialization");
        bool schemaValid = schemaProblem is null;
        deltas.Add(new("$schema", boundary == "initialization" ? "exact-owner-additive-ddl-and-index-replacements" : "unchanged-schema",
            schemaValid ? "permitted" : "unexpected", before.Inventory.Schema.RowCount, after.Inventory.Schema.RowCount,
            before.Inventory.Schema, after.Inventory.Schema, null));
        if (!schemaValid)
        {
            deltas[0] = deltas[0] with { Rule = deltas[0].Rule + "; " + schemaProblem };
            Detail(new { table = "$schema", status = "unexpected", reason = schemaProblem, before = before.Inventory.Schema, after = after.Inventory.Schema });
        }
        using CutoverDelta? cutover = boundary is "cutover" or "guard"
            ? new(source, target, started, finished, activation, guard) : null;
        foreach (PlannerRetainedTable previous in before.Inventory.Tables)
        {
            ct.ThrowIfCancellationRequested();
            PlannerRetainedTable? current = after.Inventory.Tables.SingleOrDefault(table => table.Name == previous.Name);
            if (previous.Name == "sqlite_schema")
            {
                // Not a whole-table exemption: every schema row, old row identity,
                // rootpage, SQL definition and index metadata was checked above.
                continue;
            }
            if (current is null)
            {
                deltas.Add(new(previous.Name, "table-preserved", "unexpected", previous.Rows.RowCount, 0, previous.Rows, null, null));
                Detail(new { table = previous.Name, status = "unexpected", kind = "table-removed" });
                continue;
            }
            PlannerRetainedRows? expected = null;
            bool valid;
            string rule;
            if (boundary == "initialization")
            {
                (expected, rule) = ValidateInitializationDelta(paths, source, previous, current, after.Name, ct);
                valid = SameRows(expected, current.Rows);
            }
            else if (boundary == "recovery")
            {
                (expected, rule) = ValidateRecoveryDelta(paths, source, previous, after.Name, ct);
                valid = SameRows(expected, current.Rows);
            }
            else if (cutover is not null)
            {
                rule = "exact-pre-stage-coordinator-and-participant-transition";
                valid = ValidateCutoverDelta(before, after, previous, current, cutover, details, ct);
            }
            else
            {
                rule = "all-typed-values-and-row-identities-equal";
                valid = SameRows(previous.Rows, current.Rows);
            }
            deltas.Add(new(previous.Name, rule, valid ? "permitted" : "unexpected",
                previous.Rows.RowCount, current.Rows.RowCount, previous.Rows, current.Rows, expected));
            if (!SameRows(previous.Rows, current.Rows))
            {
                WriteDifferences(before.Root, previous.Rows, after.Root, current.Rows, previous.Name,
                    valid ? "permitted" : "unexpected", details, ct);
            }
            if (!valid)
            {
                Detail(new { table = previous.Name, status = "unexpected", rule, expected, actual = current.Rows });
            }
        }
        foreach (PlannerRetainedTable added in after.Inventory.Tables.Where(table =>
                     !before.Inventory.Tables.Any(previous => previous.Name == table.Name)))
        {
            bool valid = boundary == "initialization" && SchemaContract.TryGetValue(added.Name, out SchemaObject? contract) &&
                contract.Type == "table" && added.Rows.RowCount == 0;
            deltas.Add(new(added.Name, "new-owner-table-empty", valid ? "permitted" : "unexpected", 0,
                added.Rows.RowCount, null, added.Rows, null));
            Detail(new { table = added.Name, status = valid ? "permitted" : "unexpected", kind = "new-table", rows = added.Rows });
        }
        if (cutover is not null && !cutover.Valid)
        {
            deltas.Add(new("$cutover", "pre-stage-eligibility-and-exact-allocation", "unexpected", 0, 0, null, null, null));
        }
        details.Flush();
        detailFile.Flush(flushToDisk: true);
        return deltas;

        void Detail<T>(T value) => details.WriteLine(JsonSerializer.Serialize(value));
    }

    private static (PlannerRetainedRows, string) ValidateInitializationDelta(
        PlannerRetainedStatePaths paths, SqliteConnection source, PlannerRetainedTable before,
        PlannerRetainedTable after, string stage, CancellationToken ct)
    {
        if (before.Rows.Columns.SequenceEqual(after.Rows.Columns) && (before.Name != RunsTable || before.Rows.RowCount == 0))
        {
            return (before.Rows, "all-typed-values-and-row-identities-equal");
        }
        string token = "t" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(before.Name)))[..16].ToLowerInvariant();
        // Names never enter a filesystem path. A fixed digest token keeps the
        // fully expanded budget bounded; CreateNew still refuses a collision.
        string relative = $"checkpoints/{stage}/expected/{token}.rows";
        List<string> expressions = [];
        foreach (string column in after.Rows.Columns)
        {
            if (before.Name == RunsTable && column == "Purpose")
            {
                expressions.Add(PurposeExpression(source, before) + " AS " + Quote(column));
            }
            else if (before.Rows.Columns.Contains(column, StringComparer.Ordinal))
            {
                expressions.Add(Quote(column));
            }
            else if (Additions.TryGetValue(before.Name, out var additions) &&
                additions.SingleOrDefault(addition => addition.Column == column) is var addition && addition.Column is not null)
            {
                string value = addition.Definition.Contains("DEFAULT", StringComparison.Ordinal)
                    ? addition.Definition.Split("DEFAULT ")[1] : "NULL";
                expressions.Add(value + " AS " + Quote(column));
            }
            else
            {
                return (before.Rows, "unrecognized-column-addition");
            }
        }
        return (PlannerRetainedStateInventory.WriteRows(source, paths, relative, SelectRows(before, expressions), ct),
            before.Name == RunsTable ? "ordered-purpose-predicates-only;other-values-equal" : "additive-defaults-only;old-values-equal");
    }

    private static string PurposeExpression(SqliteConnection source, PlannerRetainedTable runs)
    {
        string purpose = runs.Columns.Any(column => column.Name == "Purpose") ? "Purpose" : "'authoring'";
        string membership = PlannerRetainedStateInventory.TableExists(source, ItemsTable) ? """
            DatabaseOnly = 1
            AND EXISTS(SELECT 1 FROM authoring_run_items item WHERE item.RunId = authoring_runs.Id)
            AND NOT EXISTS(SELECT 1 FROM authoring_run_items item WHERE item.RunId = authoring_runs.Id
                AND (item.Status <> 'complete' OR item.ItemKind NOT LIKE 'maintenance:%'))
            """ : "0";
        string mode = PlannerRetainedStateInventory.TableExists(source, ModeTable)
            ? "EXISTS(SELECT 1 FROM authoring_processor_modes mode WHERE mode.RevalidationRunId = authoring_runs.Id)" : "0";
        string lineage = PlannerRetainedStateInventory.TableExists(source, "authoring_revalidation_lineage")
            ? "EXISTS(SELECT 1 FROM authoring_revalidation_lineage lineage WHERE lineage.RunId = authoring_runs.Id OR lineage.PreviousRunId = authoring_runs.Id)" : "0";
        return $"CASE WHEN {purpose} = 'authoring' AND ({membership}) THEN 'grouping-maintenance' " +
            $"WHEN {purpose} = 'authoring' AND ({mode} OR {lineage}) THEN 'initial-revalidation' ELSE {purpose} END";
    }

    private static (PlannerRetainedRows, string) ValidateRecoveryDelta(
        PlannerRetainedStatePaths paths, SqliteConnection source, PlannerRetainedTable before, string stage, CancellationToken ct)
    {
        if (before.Name != FencesTable)
        {
            return (before.Rows, "all-typed-values-and-row-identities-equal");
        }
        string generation = MaintenanceGeneration();
        string predicate = "ProcessorKind = 'jira-fhir' AND RunId LIKE 'maintenance:%' AND RunId NOT LIKE " +
            Literal($"maintenance:{generation}:%");
        return (PlannerRetainedStateInventory.WriteRows(source, paths,
                $"checkpoints/{stage}/expected/fences.rows", SelectRows(before, where: "NOT (" + predicate + ")"), ct),
        $"only-jira-fhir-old-maintenance-generation-fences-removed; ownerPrefix=maintenance:{generation}:%");
    }

    private static string MaintenanceGeneration()
    {
        // Observe, never set or mint, the unchanged owner's private process-generation
        // coordinate. The complete binary catalog pins this read-only contract.
        FieldInfo field = typeof(PlannerDatabase).GetField("MaintenanceOwnerGeneration", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw Incomplete("recovery", "owner-generation-contract-unavailable");
        return field.IsInitOnly && field.GetValue(null) is string generation && Guid.TryParseExact(generation, "N", out _)
            ? generation : throw Incomplete("recovery", "owner-generation-contract-unavailable");
    }

    private sealed record Cell(int Type, long Length, string Sha256, [property: System.Text.Json.Serialization.JsonIgnore] byte[]? Bytes)
    {
        internal static Cell Of(object? value)
        {
            int type;
            byte[] bytes;
            switch (value)
            {
                case null or DBNull: type = 5; bytes = []; break;
                case string text: type = 3; bytes = Encoding.UTF8.GetBytes(text); break;
                case byte[] blob: type = 4; bytes = blob; break;
                case double real: type = 2; bytes = BitConverter.GetBytes(real); break;
                default: type = 1; bytes = BitConverter.GetBytes(Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            }
            return new(type, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)), bytes);
        }

        internal bool Same(Cell other) => Type == other.Type && Length == other.Length && Sha256 == other.Sha256;
        internal string Text() => Type == 3 && Bytes is not null
            ? new UTF8Encoding(false, true).GetString(Bytes) : throw Incomplete("classifier", "unsupported-policy-coordinate");
        internal long Integer() => Type == 1 && Length == 8 && Bytes is not null
            ? BinaryPrimitives.ReadInt64LittleEndian(Bytes) : throw Incomplete("classifier", "unsupported-policy-coordinate");
        internal object? Value() => Type switch
        {
            5 => null, 1 => Integer(), 3 => Text(),
            _ => throw Incomplete("classifier", "unsupported-policy-coordinate"),
        };
    }

    private sealed class Row(IReadOnlyList<string> columns, Cell[] cells)
    {
        internal IReadOnlyList<string> Columns { get; } = columns;
        internal Cell[] Cells { get; } = cells;
        internal Cell this[string column]
        {
            get
            {
                int index = Columns.ToList().IndexOf(column);
                return index >= 0 ? Cells[index] : throw Incomplete("classifier", "missing-policy-column");
            }
            set
            {
                int index = Columns.ToList().IndexOf(column);
                if (index < 0) throw Incomplete("classifier", "missing-policy-column");
                // SQLite reports an INTEGER PRIMARY KEY alias's declared name
                // for both SELECT _rowid_ and SELECT RowId. Preserve and validate
                // BOTH serialized coordinates, not just the first duplicate name.
                for (int ordinal = index; ordinal < Columns.Count; ordinal++)
                {
                    if (Columns[ordinal] == column) Cells[ordinal] = value;
                }
            }
        }
        internal Row Copy() => new(Columns, (Cell[])Cells.Clone());
        internal bool Same(Row other) => Columns.SequenceEqual(other.Columns) &&
            Cells.Length == other.Cells.Length && Cells.Zip(other.Cells).All(pair => pair.First.Same(pair.Second));
    }

    // At most one row's scalar coordinates are retained. Arbitrarily large
    // TEXT/BLOB values are hashed in chunks and are still compared exactly by
    // storage class, byte length and digest; no table-sized managed row dump.
    private sealed class RowsReader : IDisposable
    {
        private readonly FileStream _file;
        private readonly BinaryReader _reader;
        private readonly PlannerRetainedRows _expected;
        private readonly CancellationToken _ct;
        private long _count;
        private bool _ended;
        internal IReadOnlyList<string> Columns { get; }

        internal RowsReader(string root, PlannerRetainedRows rows, CancellationToken ct)
        {
            _file = PlannerRetainedStatePaths.OpenFrozenFile(PlannerRetainedStatePaths.Under(root, rows.Path));
            _reader = new(_file, Encoding.UTF8, leaveOpen: true);
            _expected = rows;
            _ct = ct;
            try
            {
                if (!_reader.ReadBytes(8).SequenceEqual("FAROWS1\0"u8.ToArray()))
                {
                    throw Incomplete("classifier", "invalid-typed-rows");
                }
                int count = _reader.ReadInt32();
                if (count != rows.Columns.Count)
                {
                    throw Incomplete("classifier", "invalid-typed-rows");
                }
                List<string> columns = [];
                for (int column = 0; column < count; column++)
                {
                    long length = _reader.ReadInt64();
                    if (length < 0 || length > 65536)
                    {
                        throw Incomplete("classifier", "unsupported-column-name");
                    }
                    columns.Add(new UTF8Encoding(false, true).GetString(_reader.ReadBytes((int)length)));
                }
                if (!columns.SequenceEqual(rows.Columns))
                {
                    throw Incomplete("classifier", "invalid-typed-rows");
                }
                Columns = columns;
            }
            catch
            {
                _reader.Dispose();
                _file.Dispose();
                throw;
            }
        }

        internal Row? Read()
        {
            if (_ended) return null;
            _ct.ThrowIfCancellationRequested();
            byte marker = _reader.ReadByte();
            if (marker == 0)
            {
                _ended = true;
                if (_reader.ReadInt64() != _count || _count != _expected.RowCount || _file.Position != _file.Length)
                {
                    throw Incomplete("classifier", "invalid-typed-rows");
                }
                return null;
            }
            if (marker != 1) throw Incomplete("classifier", "invalid-typed-rows");
            Cell[] cells = new Cell[Columns.Count];
            byte[] buffer = new byte[65536];
            for (int column = 0; column < cells.Length; column++)
            {
                int type = _reader.ReadByte();
                long length = _reader.ReadInt64();
                if (type is < 1 or > 5 || length < 0 || length > _file.Length - _file.Position ||
                    (type is 1 or 2 && length != 8) || (type == 5 && length != 0))
                {
                    throw Incomplete("classifier", "invalid-typed-rows");
                }
                byte[]? scalar = length <= 65536 ? new byte[(int)length] : null;
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long remaining = length;
                while (remaining != 0)
                {
                    _ct.ThrowIfCancellationRequested();
                    int take = (int)Math.Min(buffer.Length, remaining);
                    _file.ReadExactly(buffer.AsSpan(0, take));
                    hash.AppendData(buffer, 0, take);
                    if (scalar is not null) buffer.AsSpan(0, take).CopyTo(scalar);
                    remaining -= take;
                }
                cells[column] = new(type, length, Convert.ToHexString(hash.GetHashAndReset()), scalar);
            }
            _count++;
            return new(Columns, cells);
        }

        public void Dispose()
        {
            _reader.Dispose();
            _file.Dispose();
        }
    }

    private static void WriteDifferences(
        string beforeRoot, PlannerRetainedRows before, string afterRoot, PlannerRetainedRows after,
        string table, string status, StreamWriter writer, CancellationToken ct)
    {
        using RowsReader left = new(beforeRoot, before, ct);
        using RowsReader right = new(afterRoot, after, ct);
        long ordinal = 0;
        while (true)
        {
            Row? a = left.Read();
            Row? b = right.Read();
            if (a is null && b is null) break;
            foreach (string column in left.Columns.Concat(right.Columns).Distinct(StringComparer.Ordinal))
            {
                Cell? old = a is not null && a.Columns.Contains(column) ? a[column] : null;
                Cell? current = b is not null && b.Columns.Contains(column) ? b[column] : null;
                if ((old is null) != (current is null) || old is not null && current is not null && !old.Same(current))
                {
                    writer.WriteLine(JsonSerializer.Serialize(new
                    {
                        table, status, ordinal, column, before = old, after = current,
                        beforeRows = before.Path, afterRows = after.Path,
                    }));
                }
            }
            ordinal++;
        }
    }

    private sealed record Mode(long RowId, string Value, long Epoch, long Required, string? RunId, string UpdatedAt);
    private sealed record PlanFacts(
        string Key, string GraphHash, bool CurrentReceiptBacked, bool HasState,
        string? ItemKind, string? Revision, object? CompletionId, object? CompletedAt);

    private static Mode? ReadMode(SqliteConnection connection)
    {
        if (!PlannerRetainedStateInventory.TableExists(connection, ModeTable)) return null;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT RowId, Mode, Epoch, RevalidationRequired, RevalidationRunId, UpdatedAt
            FROM authoring_processor_modes WHERE ProcessorKind = 'jira-fhir'
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)) : null;
    }

    private sealed class CutoverDelta : IDisposable
    {
        internal SqliteConnection Before { get; }
        internal SqliteConnection After { get; }
        internal Mode? OriginalMode { get; }
        internal Mode? ResultMode { get; }
        internal bool Activating { get; }
        internal long Items { get; }
        internal long NewStates { get; }
        internal bool Valid { get; private set; } = true;
        private readonly string? _guard;
        private readonly bool _activation;
        private readonly DateTimeOffset _started;
        private readonly DateTimeOffset _finished;
        private IEnumerator<PlanFacts>? _newStatePlans;
        private IEnumerator<PlanFacts>? _newItemPlans;

        internal CutoverDelta(
            SqliteConnection before, SqliteConnection after, DateTimeOffset started, DateTimeOffset finished,
            bool activation, string? guard)
        {
            Before = before;
            After = after;
            _started = started;
            _finished = finished;
            _guard = guard;
            _activation = activation;
            OriginalMode = ReadMode(before);
            ResultMode = ReadMode(after);
            Activating = activation && guard is null && OriginalMode?.Value != "run-backed";
            if (Activating && OriginalMode?.Value is not (null or "legacy" or "cutting-over")) Valid = false;
            if (Activating)
            {
                foreach (PlanFacts plan in Plans(before))
                {
                    if (plan.CurrentReceiptBacked) continue;
                    Items++;
                    if (!plan.HasState) NewStates++;
                    if (plan.ItemKind is null || plan.Revision is null) Valid = false;
                }
            }
            if (ResultMode is null) Valid = false;
            if (Activating && Items != 0 && !FreshId(ResultMode?.RunId)) Valid = false;
        }

        internal long Added(string table) => table switch
        {
            ModeTable => OriginalMode is null ? 1 : 0,
            StateTable => Activating ? NewStates : 0,
            RunsTable or FencesTable => Activating && Items != 0 ? 1 : 0,
            ItemsTable => Activating ? Items : 0,
            _ => 0,
        };

        internal Row Old(string table, Row original, Row actual)
        {
            Row expected = original.Copy();
            if (table == ModeTable && original["ProcessorKind"].Text() == "jira-fhir")
            {
                SetMode(expected, actual, created: false);
            }
            else if (table == StateTable && Activating)
            {
                string key = original["TicketKey"].Text();
                PlanFacts? facts = Plan(Before, key);
                if (facts is not null && !facts.CurrentReceiptBacked)
                {
                    SetState(expected, actual, facts);
                }
            }
            return expected;
        }

        internal Row New(string table, PlannerRetainedTable schema, Row actual, long ordinal)
        {
            Row expected = Defaults(schema);
            long max = Count(Before, $"SELECT COALESCE(MAX({Quote(schema.RowIdExpression!)}), 0) FROM {Quote(table)}");
            Cell identity = Cell.Of(checked(max + ordinal + 1));
            expected.Cells[0] = identity;
            if (schema.Columns.Any(column => column.Name == "RowId")) expected["RowId"] = identity;
            switch (table)
            {
                case ModeTable:
                    expected["ProcessorKind"] = Cell.Of("jira-fhir");
                    SetMode(expected, actual, created: true);
                    break;
                case StateTable:
                    _newStatePlans ??= Plans(Before).Where(plan => !plan.HasState && !plan.CurrentReceiptBacked).GetEnumerator();
                    if (!_newStatePlans.MoveNext()) { Valid = false; break; }
                    SetState(expected, actual, _newStatePlans.Current);
                    break;
                case RunsTable:
                    expected["Id"] = Cell.Of(ResultMode?.RunId);
                    expected["ProcessorKind"] = Cell.Of("jira-fhir");
                    expected["AuthoringEpoch"] = Cell.Of(checked((OriginalMode?.Epoch ?? 0) + 1));
                    expected["Status"] = Cell.Of("running");
                    expected["Purpose"] = Cell.Of("initial-revalidation");
                    expected["DatabaseOnly"] = Cell.Of(0);
                    expected["TotalItems"] = Cell.Of(Items);
                    expected["CreatedAt"] = expected["StartedAt"] = Cell.Of(ResultMode?.UpdatedAt);
                    break;
                case ItemsTable:
                    _newItemPlans ??= Plans(Before).Where(plan => !plan.CurrentReceiptBacked).GetEnumerator();
                    if (!_newItemPlans.MoveNext()) { Valid = false; break; }
                    PlanFacts plan = _newItemPlans.Current;
                    if (!FreshId(actual["Id"].Text())) Valid = false;
                    expected["Id"] = actual["Id"];
                    expected["RunId"] = Cell.Of(ResultMode?.RunId);
                    expected["BusinessKey"] = Cell.Of(plan.Key);
                    expected["ItemKind"] = Cell.Of(plan.ItemKind);
                    expected["ExpectedSourceRevision"] = Cell.Of(plan.Revision);
                    expected["Status"] = Cell.Of("pending");
                    expected["AttemptCount"] = Cell.Of(0);
                    expected["CreatedAt"] = Cell.Of(ResultMode?.UpdatedAt);
                    break;
                case FencesTable:
                    expected["ProcessorKind"] = Cell.Of("jira-fhir");
                    expected["RunId"] = Cell.Of(ResultMode?.RunId);
                    if (!FreshId(actual["LeaseId"].Text())) Valid = false;
                    expected["LeaseId"] = actual["LeaseId"];
                    expected["AcquiredAt"] = Cell.Of(ResultMode?.UpdatedAt);
                    break;
                default:
                    Valid = false;
                    break;
            }
            return expected;
        }

        private Row Defaults(PlannerRetainedTable table)
        {
            Cell[] values = table.Rows.Columns.Select(column =>
            {
                PlannerRetainedColumn? definition = table.Columns.SingleOrDefault(candidate => candidate.Name == column);
                string? sql = definition?.DefaultSql;
                if (sql is null) return Cell.Of(null);
                if (!Regex.IsMatch(sql, @"^(NULL|-?[0-9]+|'(?:''|[^'])*')$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
                {
                    throw Incomplete("classifier", "unsupported-new-row-default");
                }
                return Cell.Of(Scalar(Before, "SELECT " + sql));
            }).ToArray();
            return new(table.Rows.Columns, values);
        }

        private void SetMode(Row expected, Row actual, bool created)
        {
            string mode = OriginalMode?.Value ?? "legacy";
            long epoch = OriginalMode?.Epoch ?? 0;
            long required = OriginalMode?.Required ?? 0;
            string? run = OriginalMode?.RunId;
            bool timestamp = created;
            if (Activating)
            {
                mode = "run-backed";
                epoch = checked(epoch + 1);
                required = Items == 0 ? 0 : 1;
                run = Items == 0 ? null : ResultMode?.RunId;
                timestamp = true;
            }
            else if (_guard == "missing-revalidation-source")
            {
                mode = "cutting-over";
                timestamp = created || OriginalMode?.Value != "cutting-over";
            }
            else if (_guard is null && _activation && OriginalMode?.Value != "run-backed")
            {
                Valid = false;
            }
            expected["Mode"] = Cell.Of(mode);
            expected["Epoch"] = Cell.Of(epoch);
            expected["RevalidationRequired"] = Cell.Of(required);
            expected["RevalidationRunId"] = Cell.Of(run);
            if (timestamp)
            {
                expected["UpdatedAt"] = Timestamp(actual["UpdatedAt"]);
            }
        }

        private void SetState(Row expected, Row actual, PlanFacts facts)
        {
            expected["TicketKey"] = Cell.Of(facts.Key);
            expected["Classification"] = Cell.Of("legacy-unverified");
            expected["GraphHash"] = Cell.Of(facts.GraphHash);
            expected["ReceiptContentHash"] = Cell.Of(null);
            expected["LegacyCompletionId"] = Cell.Of(facts.CompletionId);
            expected["LegacyCompletedProcessingAt"] = Cell.Of(facts.CompletedAt);
            expected["RunId"] = expected["RunItemId"] = expected["OperationId"] = Cell.Of(null);
            expected["UpdatedAt"] = Timestamp(actual["UpdatedAt"]);
        }

        private Cell Timestamp(Cell value)
        {
            string text = value.Text();
            if (!DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at) ||
                at.Offset != TimeSpan.Zero || at < _started || at > _finished)
            {
                Valid = false;
            }
            return value;
        }

        private bool FreshId(string? value)
        {
            if (value is null || !Guid.TryParseExact(value, "N", out Guid id) || id.ToString("N") != value ||
                value[12] != '4' || !"89ab".Contains(value[16]))
            {
                return false;
            }
            foreach ((string table, string column) in new[]
            {
                (RunsTable, "Id"), (ItemsTable, "Id"), ("authoring_result_receipts", "Id"),
                ("authoring_run_attempts", "OperationId"), (FencesTable, "LeaseId"),
            })
            {
                if (Count(Before, $"SELECT COUNT(*) FROM {Quote(table)} WHERE {Quote(column)} = @id", ("@id", value)) != 0)
                {
                    return false;
                }
            }
            return Count(After, """
                SELECT (SELECT COUNT(*) FROM authoring_runs WHERE Id = @id) +
                       (SELECT COUNT(*) FROM authoring_run_items WHERE Id = @id) +
                       (SELECT COUNT(*) FROM authoring_mutation_fences WHERE LeaseId = @id)
                """, ("@id", value)) == 1;
        }

        public void Dispose()
        {
            _newStatePlans?.Dispose();
            _newItemPlans?.Dispose();
        }
    }

    private static bool ValidateCutoverDelta(
        Snapshot before, Snapshot after, PlannerRetainedTable original, PlannerRetainedTable current,
        CutoverDelta scope, StreamWriter details, CancellationToken ct)
    {
        if (!original.Rows.Columns.SequenceEqual(current.Rows.Columns))
        {
            return false;
        }
        long added = scope.Added(original.Name);
        if (SameRows(original.Rows, current.Rows) && added == 0 &&
            original.Name is not (ModeTable or StateTable))
        {
            return true;
        }
        bool valid = current.Rows.RowCount == original.Rows.RowCount + added;
        using RowsReader left = new(before.Root, original.Rows, ct);
        using RowsReader right = new(after.Root, current.Rows, ct);
        long ordinal = 0;
        Row? old;
        while ((old = left.Read()) is not null)
        {
            Row? actual = right.Read();
            if (actual is null)
            {
                valid = false;
                continue;
            }
            Row expected = scope.Old(original.Name, old, actual);
            if (!expected.Same(actual))
            {
                valid = false;
                details.WriteLine(JsonSerializer.Serialize(new
                {
                    table = original.Name, ordinal, status = "unexpected", kind = "preexisting-row-not-exact-expected-transition",
                }));
            }
            ordinal++;
        }
        long addition = 0;
        Row? appended;
        while ((appended = right.Read()) is not null)
        {
            if (addition >= added || current.RowIdExpression is null ||
                !scope.New(original.Name, original, appended, addition).Same(appended))
            {
                valid = false;
                details.WriteLine(JsonSerializer.Serialize(new
                {
                    table = original.Name, ordinal, status = "unexpected", kind = "new-row-not-exact-pre-stage-membership",
                }));
            }
            ordinal++;
            addition++;
        }
        return valid && scope.Valid;
    }

    private static IEnumerable<PlanFacts> Plans(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Key FROM planned_tickets ORDER BY Key";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return Plan(connection, reader.GetString(0))!;
        }
    }

    private static PlanFacts? Plan(SqliteConnection connection, string key)
    {
        string? actualKey = Scalar(connection, "SELECT Key FROM planned_tickets WHERE Key = @key", ("@key", key)) as string;
        if (actualKey is null) return null;
        string graphHash = GraphHash(connection, actualKey);
        bool receiptBacked = Count(connection, """
            SELECT COUNT(*) FROM planned_ticket_authoring_state s
            INNER JOIN authoring_result_receipts r
                ON r.OperationId = s.OperationId AND r.RunId = s.RunId AND r.RunItemId = s.RunItemId
                AND r.BusinessKey = s.TicketKey COLLATE NOCASE AND r.ContentHash = s.ReceiptContentHash
                AND r.ObservedSourceRevision = r.ExpectedSourceRevision
            INNER JOIN authoring_run_items i
                ON i.Id = s.RunItemId AND i.RunId = s.RunId AND i.BusinessKey = s.TicketKey COLLATE NOCASE
                AND i.ItemKind = 'fhir' COLLATE NOCASE AND i.AcceptedReceiptId = r.Id
                AND i.ExpectedSourceRevision = r.ExpectedSourceRevision AND i.Status IN ('complete', 'superseded')
            INNER JOIN authoring_runs run
                ON run.Id = s.RunId AND run.ProcessorKind = 'jira-fhir' AND run.AuthoringEpoch = r.AuthoringEpoch
            WHERE s.TicketKey = @key COLLATE NOCASE AND s.Classification = 'receipt-backed' AND s.GraphHash = @hash
            """, ("@key", actualKey), ("@hash", graphHash)) != 0;
        bool hasState = Count(connection, "SELECT COUNT(*) FROM planned_ticket_authoring_state WHERE TicketKey = @key",
            ("@key", actualKey)) != 0;
        string? shape = null;
        string? revision = null;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT SourceTicketShape, LastUpdated, Title, Status, WorkGroup, Type, Specification
                FROM jira_processing_source_tickets WHERE Key = @key COLLATE NOCASE AND SourceTicketShape = 'fhir' COLLATE NOCASE
                """;
            command.Parameters.AddWithValue("@key", actualKey);
            using SqliteDataReader reader = command.ExecuteReader();
            if (reader.Read())
            {
                shape = reader.GetString(0);
                revision = reader.IsDBNull(1)
                    ? AuthoringResultHasher.HashNormalizedUtf8(string.Join("\n", actualKey, reader.GetString(2),
                        reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)))
                    : DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                        .ToString("O", CultureInfo.InvariantCulture);
                if (reader.Read()) throw Incomplete("classifier", "ambiguous-revalidation-source");
            }
        }
        return new(actualKey, graphHash, receiptBacked, hasState, shape, revision,
            Scalar(connection, "SELECT CompletionId FROM jira_processing_source_tickets WHERE Key = @key", ("@key", actualKey)),
            Scalar(connection, "SELECT CompletedProcessingAt FROM jira_processing_source_tickets WHERE Key = @key", ("@key", actualKey)));
    }

    private static string GraphHash(SqliteConnection connection, string key)
    {
        // This read-only projection freezes eligibility BEFORE activation. It uses
        // the pinned participant's graph serialization, not its mutating classifier.
        (string Section, string Sql, int? Replacement)[] queries =
        [
            ("ticket", "SELECT Key, Resolution, ResolutionSummary, FeatureProposal, DesignRationale FROM planned_tickets WHERE Key = @key", null),
            ("repos", "SELECT RepoKey, RepoRevision, Justification FROM planned_ticket_repos WHERE IssueKey = @key ORDER BY RepoKey, RepoRevision, Justification", null),
            ("changes", "SELECT RepoKey, ChangeSequence, FilePath, ChangeTitle, ChangeDescription, SourceLineStart, SourceLineEnd, ReplacementLines, Reason FROM planned_ticket_repo_changes WHERE IssueKey = @key ORDER BY RepoKey, ChangeSequence, FilePath", 7),
            ("impacts", "SELECT RepoKey, AffectedFilePath, HowAffected FROM planned_ticket_repo_impacts WHERE IssueKey = @key ORDER BY RepoKey, AffectedFilePath", null),
            ("validations", "SELECT RepoKey, ValidationSequence, Action FROM planned_ticket_change_validations WHERE IssueKey = @key ORDER BY RepoKey, ValidationSequence", null),
            ("testing", "SELECT RepoKey, ConsiderationSequence, Consideration FROM planned_ticket_testing_considerations WHERE IssueKey = @key ORDER BY RepoKey, ConsiderationSequence", null),
            ("questions", "SELECT RepoKey, QuestionSequence, Question FROM planned_ticket_open_questions WHERE IssueKey = @key ORDER BY RepoKey, QuestionSequence", null),
        ];
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach ((string section, string sql, int? replacement) in queries)
        {
            Append("section:" + section + "\n");
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@key", key);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                for (int column = 0; column < reader.FieldCount; column++)
                {
                    if (reader.IsDBNull(column)) Append("<null>");
                    else if (replacement == column) Append(ReplacementLineJson.Serialize(ReplacementLineJson.Deserialize(ReadText(reader, column))));
                    else if (reader.GetFieldType(column) == typeof(string)) Append(ReadText(reader, column));
                    else Append(reader.GetValue(column).ToString() ?? "");
                    Append("\u001f");
                }
                Append("\n");
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Append(string value)
        {
            // The pinned serializer separates every value by a control character,
            // a normalization boundary. NFC/CRLF normalization therefore composes
            // per scalar, without retaining a whole graph in a StringBuilder.
            string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                .Normalize(NormalizationForm.FormC);
            Encoder encoder = Encoding.UTF8.GetEncoder();
            ReadOnlySpan<char> remaining = normalized.AsSpan();
            byte[] buffer = new byte[65536];
            while (!remaining.IsEmpty)
            {
                encoder.Convert(remaining, buffer, flush: true, out int characters, out int bytes, out _);
                hash.AppendData(buffer, 0, bytes);
                remaining = remaining[characters..];
            }
        }

        static string ReadText(SqliteDataReader reader, int column)
        {
            using TextReader text = reader.GetTextReader(column);
            StringBuilder scalar = new();
            char[] buffer = new char[32768];
            int count;
            while ((count = text.Read(buffer)) != 0)
            {
                if (scalar.Length + count > 16 * 1024 * 1024)
                {
                    throw Incomplete("classifier", "unsupported-graph-scalar-reader");
                }
                scalar.Append(buffer, 0, count);
            }
            return scalar.ToString();
        }
    }

    private async Task<PlannerRetainedRehearsalGuard?> ProveGuardAsync(
        PlannerRetainedStatePaths paths, PlannerRetainedRehearsalMap map, Snapshot before, Snapshot failed,
        string stage, Exception exception, DateTimeOffset started, CancellationToken ct)
    {
        if (exception is OperationCanceledException or PlannerRetainedStateException) return null;
        using SqliteConnection source = await OpenSnapshotAsync(before, ct);
        using SqliteConnection failure = await OpenSnapshotAsync(failed, ct);
        if (stage == "initialization" && exception.GetType() == typeof(InvalidOperationException) &&
            SourceAdmissionFailure(source, exception, out string? sourceMethod, out string? signature))
        {
            return new("schema-admission-refused", sourceMethod!, before.Name, failed.Name,
                signature!, null, null, null, null);
        }
        if (stage != "cutover") return null;
        Mode? oldMode = ReadMode(source);
        Mode? newMode = ReadMode(failure);
        if (oldMode is { Value: not ("legacy" or "cutting-over" or "run-backed") } &&
            HasFrame(exception, typeof(AuthoringCutoverCoordinator), "ActivateAsync") &&
            exception.GetType() == typeof(InvalidOperationException) &&
            exception.Message == $"Unknown authoring mode '{oldMode.Value}'." && oldMode == newMode)
        {
            return new("unknown-processor-mode", "AuthoringCutoverCoordinator.ActivateAsync", before.Name, failed.Name,
                "Pre-stage mode is outside legacy/cutting-over/run-backed; exact mode row is unchanged.", null, null, null, null);
        }
        if (map.PreCutoverBackup is null || oldMode?.Value is not (null or "legacy" or "cutting-over")) return null;
        string backupPath = paths.InBundle(map.PreCutoverBackup);
        bool exists = PlannerRetainedStatePaths.TryAttributes(backupPath, out _);
        PlannerRetainedEvidenceFile? backup = exists ? PlannerRetainedStateEvidence.Fingerprint(paths.Bundle, backupPath, ct) : null;
        Mode? backupMode = null;
        bool readable = false;
        bool integrity = false;
        bool hasModeTable = false;
        if (exists)
        {
            try
            {
                using SqliteConnection reader = OpenOrdinaryReadOnly(backupPath);
                readable = true;
                hasModeTable = PlannerRetainedStateInventory.TableExists(reader, ModeTable);
                backupMode = ReadMode(reader);
                using SqliteCommand check = reader.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                using SqliteDataReader rows = check.ExecuteReader();
                integrity = rows.Read() && rows.GetString(0) == "ok" && !rows.Read();
            }
            catch (SqliteException)
            {
                readable = false;
            }
        }
        bool operational = readable && integrity && backupMode is { Value: "legacy", Epoch: 0, Required: 0, RunId: null };
        string modeFact = backupMode is null ? "absent" :
            $"{backupMode.Value};epoch={backupMode.Epoch};revalidationRequired={backupMode.Required};runId={backupMode.RunId ?? "null"}";
        if (exception.GetType() == typeof(InvalidOperationException) &&
            exception.Message == "An existing pre-cutover backup does not match the current committed legacy database." &&
            HasFrame(exception, typeof(AuthoringCutoverCoordinator), "CreateOrVerifyBackupAsync") &&
            oldMode?.Value is null or "legacy" && newMode is { Value: "legacy" } && operational && backup is not null)
        {
            checkpoint?.Invoke("before-backup-diagnostic", paths.InBundle(map.Database));
            string diagnostic = paths.Output("diagnostics/cutover-candidate.db");
            using (PlannerRetainedStatePaths.CreateFile(diagnostic))
            {
            }
            // Regenerate ONCE from the frozen failure checkpoint. This is not
            // activation, not a replacement backup, and not the discarded temp.
            using (SqliteConnection destination = OpenWritableCopy(diagnostic))
            {
                failure.BackupDatabase(destination);
            }
            PlannerRetainedEvidenceFile candidate = PlannerRetainedStateEvidence.Fingerprint(paths.Bundle, diagnostic, ct);
            if (candidate.Sha256 == backup.Sha256)
            {
                throw Incomplete("diagnostics", "backup-mismatch-not-reproduced-by-frozen-state");
            }
            PlannerRetainedRehearsalGuard proof = new("backup-hash-mismatch",
                "AuthoringCutoverCoordinator.CreateOrVerifyBackupAsync", before.Name, failed.Name,
                "Operational legacy backup remains byte-identical; regenerated frozen-state candidate has a different SHA256.",
                backup, modeFact, candidate, "separately-regenerated-BackupDatabase;not-coordinator-temp");
            PlannerRetainedStateEvidence.WriteJson(paths.Output("diagnostics/backup-guard.json"), proof);
            return proof;
        }
        bool verificationFrame = HasFrame(exception, typeof(AuthoringCutoverCoordinator), "VerifyBackupAsync");
        bool exactInvalidGuard = exception.GetType() == typeof(InvalidOperationException) &&
            ((!exists && exception.Message == $"The verified pre-cutover backup is missing: {backupPath}") ||
             (exists && backupMode is null && hasModeTable &&
                exception.Message == "Pre-cutover backup does not contain processor mode metadata.") ||
             (exists && backupMode is not null && !operational &&
                exception.Message == "Pre-cutover backup is not an operational legacy database."));
        bool sqlInvalidGuard = exception is SqliteException sql && verificationFrame &&
            ((!readable && sql.SqliteErrorCode is 11 or 26) ||
             (!hasModeTable && sql.SqliteErrorCode == 1 && HasFrame(exception, typeof(AuthoringCutoverCoordinator), "ReadModeAsync")));
        if (verificationFrame && !operational && (exactInvalidGuard || sqlInvalidGuard))
        {
            return new("backup-missing-or-invalid", "AuthoringCutoverCoordinator.VerifyBackupAsync", before.Name, failed.Name,
                $"Mapped backup exists={exists}; readable={readable}; integrity={integrity}; processorModeTable={hasModeTable}.",
                backup, modeFact, null, null);
        }
        if (exception.GetType() == typeof(InvalidOperationException) &&
            HasFrame(exception, typeof(PlannerDatabase), "PrepareCutoverAsync") && newMode?.Value == "cutting-over" && operational)
        {
            PlanFacts? missing = Plans(source).FirstOrDefault(plan => !plan.CurrentReceiptBacked && plan.ItemKind is null);
            if (missing is not null &&
                exception.Message == $"Planned ticket '{missing.Key}' has no current Jira source row for revalidation.")
            {
                return new("missing-revalidation-source", "PlannerDatabase.PrepareCutoverAsync", before.Name, failed.Name,
                    "The first pre-stage non-receipt-backed plan without a current FHIR source matches the exact guard; participant transaction rolled back.",
                    backup, modeFact, null, null);
            }
        }
        return null;
    }

    private static bool HasFrame(Exception exception, Type type, string method)
    {
        foreach (StackFrame frame in new StackTrace(exception).GetFrames())
        {
            MethodBase? actual = frame.GetMethod();
            if (actual?.DeclaringType == type && actual.Name == method) return true;
            if (actual?.Name == "MoveNext" && type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Any(candidate => candidate.Name == method &&
                        candidate.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?.StateMachineType == actual.DeclaringType))
            {
                return true;
            }
        }
        return false;
    }

    private static bool SourceAdmissionFailure(
        SqliteConnection baseline, Exception actual, out string? sourceMethod, out string? signature)
    {
        sourceMethod = null;
        signature = null;
        string[] guards = ["ValidateExistingSourceTicketSchema", "ValidateCompositeIndex", "ValidateRetainedIdentities", "ValidateCompletionIndex"];
        if (!guards.Any(method => HasFrame(actual, typeof(JiraProcessingSourceTicketStore), method))) return false;
        // Only the unchanged transaction-free admission readers are invoked here,
        // against the frozen PRE-stage snapshot with query_only. No initializer,
        // DDL, writer reservation, data repair or candidate rerun supplies an oracle.
        foreach (string name in guards)
        {
            if (name == "ValidateRetainedIdentities" && !PlannerRetainedStateInventory.TableExists(baseline, SourceTable)) continue;
            MethodInfo method = typeof(JiraProcessingSourceTicketStore).GetMethod(
                name, BindingFlags.NonPublic | BindingFlags.Static, [typeof(SqliteConnection)])
                ?? throw Incomplete("guard-proof", "source-admission-reader-unavailable");
            try
            {
                _ = method.Invoke(null, [baseline]);
            }
            catch (TargetInvocationException invoked) when (invoked.InnerException is InvalidOperationException proof &&
                proof.GetType() == typeof(InvalidOperationException))
            {
                string? expected = AdmissionSignature(proof.Message);
                if (expected is not null && expected == AdmissionSignature(actual.Message) &&
                    HasFrame(actual, typeof(JiraProcessingSourceTicketStore), name))
                {
                    sourceMethod = "JiraProcessingSourceTicketStore." + name;
                    signature = expected;
                    return true;
                }
                return false;
            }
        }
        return false;

        static string? AdmissionSignature(string message)
        {
            string prefix = $"Cannot initialize table '{SourceTable}' in database '";
            if (!message.StartsWith(prefix, StringComparison.Ordinal)) return null;
            int boundary = message.IndexOf("': ", prefix.Length, StringComparison.Ordinal);
            return boundary < 0 ? null : message[(boundary + 3)..];
        }
    }

    private static PlannerRetainedStateException Incomplete(string stage, string category, Exception? inner = null) =>
        new(stage, category, 1, inner);
}
