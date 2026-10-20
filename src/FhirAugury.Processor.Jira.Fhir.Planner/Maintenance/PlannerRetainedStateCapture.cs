using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Maintenance;

internal sealed class PlannerRetainedStateCapture(Action<string>? checkpoint = null)
{
    private sealed record FrozenFile(PlannerRetainedMember Member, FileStream File, string Sha256);
    private sealed record SafetyCopy(string ArtifactId, string Path);

    internal async Task<PlannerRetainedStateManifest> CaptureAsync(
        PlannerRetainedStateSettings settings, CancellationToken ct = default, bool configureProcessTemp = false)
    {
        ValidateSettings(settings);
        PlannerRetainedStatePaths? paths = null;
        PlannerDatabase? owner = null;
        SafeFileHandle? ownerIdentityPin = null;
        List<FrozenFile> frozen = [];
        string stage = "paths";
        string? previousTemp = null;
        string? previousTmp = null;
        bool tempConfigured = false;
        try
        {
            paths = PlannerRetainedStatePaths.Admit(settings);
            paths.CreateBundle();
            PlannerRetainedStateEvidence.WriteJson(paths.Output("capture.started.json"), new
            {
                formatVersion = 1, status = "incomplete", completionAuthority = "capture.complete.json",
                settings, roots = paths.Roots, settingsPaths = paths.SettingsPaths,
            });
            ct.ThrowIfCancellationRequested();
            stage = "ownership";
            bool lockExisted = PlannerRetainedStatePaths.TryAttributes(paths.OwnerLockPath, out _);
            PlannerRetainedOwnerLock ownerLock;
            if (lockExisted)
            {
                ownerIdentityPin = PlannerRetainedStatePaths.PinFileIdentity(paths.OwnerLockPath);
                using FileStream coordination = PlannerRetainedStatePaths.OpenFrozenFile(paths.OwnerLockPath);
                ownerLock = new(true, false, coordination.Length, PlannerRetainedStateEvidence.Hash(coordination, ct),
                    PlannerRetainedStatePaths.Identify(coordination.SafeFileHandle, paths.OwnerLockPath));
            }
            else
            {
                ownerLock = new(false, true, 0, null, null);
            }
            // Construction only builds a non-pooled, read-only connection string.
            // This instance is NEVER initialized, recovered, activated or SQLite-opened.
            owner = new(settings.Database, NullLogger<PlannerDatabase>.Instance, readOnly: true);
            try
            {
                owner.AcquireStartupOwnership();
            }
            catch (InvalidOperationException ex) when (ex.InnerException is IOException)
            {
                throw new PlannerRetainedStateException(stage, "owner-or-writer-busy", inner: ex);
            }
            ownerIdentityPin ??= PlannerRetainedStatePaths.PinFileIdentity(paths.OwnerLockPath);
            PlannerRetainedFileIdentity acquiredLockIdentity =
                PlannerRetainedStatePaths.Identify(ownerIdentityPin, paths.OwnerLockPath);
            if (ownerLock.PreviousIdentity is { } previousIdentity && acquiredLockIdentity != previousIdentity)
            {
                throw Refuse(stage, "owner-lock-identity-changed");
            }
            if (PlannerRetainedStatePaths.InspectFile(settings.Database) != paths.DatabaseIdentity)
            {
                throw Refuse(stage, "database-identity-changed");
            }
            checkpoint?.Invoke("ownership-acquired");
            stage = "freeze";
            PlannerRetainedMembership membership = paths.Enumerate();
            paths.ValidateExpandedBudgets(membership);
            foreach (PlannerRetainedMember member in membership.Files)
            {
                ct.ThrowIfCancellationRequested();
                FileStream file = PlannerRetainedStatePaths.OpenFrozenFile(member.Path);
                try
                {
                    if (PlannerRetainedStatePaths.Identify(file.SafeFileHandle, member.Path) != member.Identity ||
                        file.Length != member.Length)
                    {
                        throw Refuse(stage, "membership-changed");
                    }
                    frozen.Add(new(member, file, PlannerRetainedStateEvidence.Hash(file, ct)));
                }
                catch
                {
                    file.Dispose();
                    throw;
                }
            }
            checkpoint?.Invoke("files-frozen");
            Recheck(paths, membership, frozen, ct);

            stage = "raw-copy";
            foreach (PlannerRetainedDirectory directory in membership.Directories)
            {
                paths.EnsureOutputDirectory(paths.InBundle("raw/" + directory.RootId + "/" + directory.RelativePath));
            }
            List<PlannerRetainedArtifact> artifacts = [];
            foreach (FrozenFile input in frozen)
            {
                string relative = "raw/" + input.Member.RootId + "/" + input.Member.RelativePath;
                string output = paths.Output(relative);
                Copy(input.File, output, ct);
                PlannerRetainedEvidenceFile raw = PlannerRetainedStateEvidence.Fingerprint(paths.Bundle, output, ct);
                if (raw.Length != input.Member.Length || raw.Sha256 != input.Sha256 ||
                    PlannerRetainedStatePaths.InspectFile(output) == input.Member.Identity)
                {
                    throw Mismatch(stage, "original-raw-preservation-mismatch");
                }
                artifacts.Add(new($"f{artifacts.Count:D6}", input.Member.RootId, input.Member.RelativePath,
                    input.Member.Path, relative, input.Member.Identity, input.Member.Length, input.Sha256, "unclassified"));
            }
            PlannerRetainedStateEvidence.WriteJson(paths.Output("raw.catalog.json"), new
            {
                formatVersion = 1, status = "frozen-raw-only", settings, paths.Roots,
                paths.SettingsPaths, ownerLock, membership.Directories, artifacts,
            });
            checkpoint?.Invoke("raw-copied");
            Recheck(paths, membership, frozen, ct);

            stage = "family-admission";
            (artifacts, List<PlannerRetainedSidecar> sidecars) = ClassifyFamilies(paths.Bundle, artifacts, settings);
            paths.ValidateExpandedBudgets(membership);
            stage = "normalization";
            string temporary = paths.InBundle("normalization/temp");
            paths.EnsureOutputDirectory(temporary);
            if (configureProcessTemp)
            {
                // Only the early, terminating command uses this process-local
                // setting. Direct synthetic helper tests do not alter process globals.
                previousTemp = Environment.GetEnvironmentVariable("TEMP");
                previousTmp = Environment.GetEnvironmentVariable("TMP");
                Environment.SetEnvironmentVariable("TEMP", temporary);
                Environment.SetEnvironmentVariable("TMP", temporary);
                tempConfigured = true;
            }
            checkpoint?.Invoke("before-sqlite");
            List<SafetyCopy> safetyCopies = [];
            foreach (PlannerRetainedArtifact database in artifacts.Where(artifact => artifact.Kind == "database"))
            {
                ct.ThrowIfCancellationRequested();
                string normalizedRelative = "normalization/" + database.RootId + "/" + database.RelativePath;
                string normalized = paths.Output(normalizedRelative);
                CopyFile(paths.InBundle(database.RawPath), normalized, ct);
                foreach (PlannerRetainedSidecar sidecar in sidecars.Where(sidecar =>
                             sidecar.DatabaseArtifactId == database.Id && sidecar.ArtifactId is not null &&
                             sidecar.Suffix != "-shm"))
                {
                    PlannerRetainedArtifact artifact = artifacts.Single(artifact => artifact.Id == sidecar.ArtifactId);
                    CopyFile(paths.InBundle(artifact.RawPath), paths.Output(normalizedRelative + sidecar.Suffix), ct);
                }
                string safetyRelative = PlannerRetainedStatePaths.SamePath(database.OriginalPath, settings.Database)
                    ? "safety/planner.db" : $"safety/d{safetyCopies.Count:D6}.db";
                string safetyPath = paths.Output(safetyRelative);
                using (PlannerRetainedStatePaths.CreateFile(safetyPath))
                {
                }
                // No immutable flag here: committed WAL frames must be consumed.
                // Captured SHM stays in raw; SQLite rebuilds a disposable WAL index.
                using (SqliteConnection source = OpenDisposable(normalized))
                using (SqliteConnection destination = OpenDisposable(safetyPath))
                {
                    PlannerRetainedStateInventory.Execute(source, "PRAGMA query_only = ON; PRAGMA trusted_schema = OFF;");
                    source.BackupDatabase(destination);
                }
                RequireStandalone(paths, safetyRelative);
                safetyCopies.Add(new(database.Id, safetyRelative));
            }

            stage = "reference-closure";
            List<PlannerRetainedSnapshotReference> references;
            HashSet<string> verifiedSnapshots;
            try
            {
                (references, verifiedSnapshots) = await ValidateSnapshotClosureAsync(paths, artifacts, sidecars, safetyCopies, ct);
            }
            catch (SqliteException ex)
            {
                throw new PlannerRetainedStateException(stage, "unsupported-table-reader", inner: ex);
            }
            stage = "inventory";
            List<PlannerRetainedDatabaseInventory> inventories = [];
            foreach (SafetyCopy safety in safetyCopies)
            {
                inventories.Add(await PlannerRetainedStateInventory.WriteAsync(
                    paths, safety.ArtifactId, safety.Path, $"inventory/d{inventories.Count:D6}",
                    verifiedSnapshots.Contains(safety.ArtifactId), ct));
            }
            checkpoint?.Invoke("inventories-written");
            stage = "completion";
            IReadOnlyList<PlannerRetainedBinary> binaries = PlannerRetainedStateEvidence.FingerprintBinaries(ct);
            PlannerRetainedStateManifest manifest = new(
                1, "complete", DateTimeOffset.UtcNow, settings.CandidateCommit, settings.CandidateTree,
                "owner-supplied-source-identities; independently-hashed-executed-binaries",
                settings, paths.Roots, paths.SettingsPaths, membership.Directories, ownerLock, artifacts,
                sidecars, inventories, references, binaries);
            PlannerRetainedStateEvidence.WriteJson(paths.Output("manifest.json"), manifest);
            checkpoint?.Invoke("manifest-written");
            Recheck(paths, membership, frozen, ct);
            VerifyRaw(paths.Bundle, artifacts, ct);
            (IReadOnlyList<PlannerRetainedEvidenceFile> catalog, IReadOnlyList<string> directories) = Catalog(paths.Bundle, ct);
            PlannerRetainedStateCompletion completion = new(
                1, "complete", settings.CandidateCommit, settings.CandidateTree,
                catalog.Single(file => file.Path == "manifest.json").Sha256, catalog, directories, binaries);
            checkpoint?.Invoke("before-completion");
            string pendingMarker = paths.Output("capture.complete.pending.json");
            PlannerRetainedStateEvidence.WriteJson(pendingMarker, completion);
            File.Move(pendingMarker, paths.Output("capture.complete.json"), overwrite: false);
            checkpoint?.Invoke("marker-written");
            using (PlannerRetainedVerifiedCapture verified = await OpenVerifiedAsync(
                       paths.Bundle, settings.CandidateCommit, settings.CandidateTree, ct))
            {
                Recheck(paths, membership, frozen, ct);
                if (PlannerRetainedStatePaths.Identify(ownerIdentityPin, paths.OwnerLockPath) != acquiredLockIdentity)
                {
                    throw Refuse("completion", "owner-lock-identity-changed");
                }
            }
            return manifest;
        }
        catch (Exception ex)
        {
            if (paths?.BundleCreated == true)
            {
                string marker = paths.InBundle("capture.complete.json");
                if (PlannerRetainedStatePaths.TryAttributes(marker, out _))
                {
                    File.Delete(marker);
                }
                PlannerRetainedStateEvidence.WriteJson(paths.Output("capture.incomplete.json"), new
                {
                    formatVersion = 1, status = "incomplete", stage,
                    category = ex is PlannerRetainedStateException refusal ? refusal.Category :
                        ex is OperationCanceledException ? "cancelled" : "unexpected-error",
                    exception = ex.ToString(),
                });
            }
            throw;
        }
        finally
        {
            foreach (FrozenFile input in frozen.AsEnumerable().Reverse())
            {
                input.File.Dispose();
            }
            ownerIdentityPin?.Dispose();
            owner?.Dispose();
            paths?.Dispose();
            if (tempConfigured)
            {
                Environment.SetEnvironmentVariable("TEMP", previousTemp);
                Environment.SetEnvironmentVariable("TMP", previousTmp);
            }
        }
    }

    internal static Task<PlannerRetainedVerifiedCapture> OpenVerifiedAsync(
        string bundle, string candidateCommit, string candidateTree, CancellationToken ct = default)
    {
        bundle = PlannerRetainedStatePaths.Absolute(bundle);
        List<IDisposable> handles = [];
        try
        {
            Dictionary<string, FileStream> files = new(StringComparer.Ordinal);
            HashSet<string> directories = new(StringComparer.Ordinal);
            Freeze(bundle);
            if (!files.ContainsKey("capture.complete.json") || !files.ContainsKey("manifest.json"))
            {
                throw Refuse("evidence", "completion-marker-missing");
            }
            PlannerRetainedStateCompletion completion = Read<PlannerRetainedStateCompletion>("capture.complete.json");
            if (completion.FormatVersion != 1 || completion.Status != "complete" ||
                completion.CandidateCommit != candidateCommit || completion.CandidateTree != candidateTree)
            {
                throw Refuse("evidence", "candidate-or-format-mismatch");
            }
            Dictionary<string, PlannerRetainedEvidenceFile> catalog = completion.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
            if (!files.Keys.Where(path => path != "capture.complete.json").Order(StringComparer.Ordinal)
                    .SequenceEqual(catalog.Keys.Order(StringComparer.Ordinal)) ||
                !directories.Order(StringComparer.Ordinal).SequenceEqual(completion.Directories.Order(StringComparer.Ordinal)))
            {
                throw Mismatch("evidence", "bundle-membership-mismatch");
            }
            foreach (PlannerRetainedEvidenceFile expected in completion.Files)
            {
                _ = PlannerRetainedStatePaths.Under(bundle, expected.Path);
                FileStream file = files[expected.Path];
                if (file.Length != expected.Length || PlannerRetainedStateEvidence.Hash(file, ct) != expected.Sha256)
                {
                    throw Mismatch("evidence", "bundle-hash-mismatch");
                }
            }
            PlannerRetainedStateManifest manifest = Read<PlannerRetainedStateManifest>("manifest.json");
            if (manifest.FormatVersion != 1 || manifest.Status != "complete" ||
                manifest.CandidateCommit != candidateCommit || manifest.CandidateTree != candidateTree ||
                manifest.Settings.CandidateCommit != candidateCommit || manifest.Settings.CandidateTree != candidateTree)
            {
                throw Refuse("evidence", "candidate-or-format-mismatch");
            }
            ValidateSettings(manifest.Settings);
            if (catalog["manifest.json"].Sha256 != completion.ManifestSha256 ||
                !PlannerRetainedStateEvidence.SameBinaries(manifest.Binaries, completion.Binaries) ||
                !PlannerRetainedStateEvidence.SameBinaries(completion.Binaries, PlannerRetainedStateEvidence.FingerprintBinaries(ct)))
            {
                throw Refuse("evidence", "manifest-or-binary-mismatch");
            }
            HashSet<string> roots = manifest.Roots.Select(root => root.Id).ToHashSet(StringComparer.Ordinal);
            if (roots.Count != manifest.Roots.Count || manifest.Roots.Any(root =>
                    !System.Text.RegularExpressions.Regex.IsMatch(root.Id, @"^r[0-9]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            {
                throw Refuse("evidence", "invalid-root-map");
            }
            HashSet<string> artifactIds = [];
            HashSet<string> originalPaths = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, PlannerRetainedRoot> rootMap = manifest.Roots.ToDictionary(root => root.Id, StringComparer.Ordinal);
            foreach (PlannerRetainedRoot root in manifest.Roots)
            {
                _ = PlannerRetainedStatePaths.AbsoluteSyntax(root.OriginalDirectory);
                if (root.Scope is not ("directory" or "file-family") ||
                    (root.Scope == "file-family" && (string.IsNullOrEmpty(root.SelectedFile) ||
                        Path.GetFileName(root.SelectedFile) != root.SelectedFile)))
                {
                    throw Refuse("evidence", "invalid-root-map");
                }
            }
            foreach (PlannerRetainedArtifact artifact in manifest.Artifacts)
            {
                if (!artifactIds.Add(artifact.Id) || !originalPaths.Add(artifact.OriginalPath) ||
                    !roots.Contains(artifact.RootId) || artifact.Identity.LinkCount != 1 ||
                    artifact.RawPath != "raw/" + artifact.RootId + "/" + artifact.RelativePath)
                {
                    throw Refuse("evidence", "invalid-artifact-map");
                }
                if (!PlannerRetainedStatePaths.SamePath(artifact.OriginalPath,
                        OriginalPath(new(artifact.RootId, artifact.RelativePath, true))))
                {
                    throw Refuse("evidence", "invalid-artifact-map");
                }
                RequireFile(artifact.RawPath, artifact.Sha256, artifact.Length);
                if (PlannerRetainedStatePaths.Identify(files[artifact.RawPath].SafeFileHandle,
                        PlannerRetainedStatePaths.Under(bundle, artifact.RawPath)) == artifact.Identity)
                {
                    throw Refuse("evidence", "physical-file-alias");
                }
            }
            ValidateSettingPath(manifest.SettingsPaths.Database, manifest.Settings.Database, requiredFile: true);
            if (manifest.SettingsPaths.PreCutoverBackup is { } backup)
            {
                ValidateSettingPath(backup, manifest.Settings.PreCutoverBackup, requiredFile: true);
            }
            else if (manifest.Settings.PreCutoverBackup.Length != 0)
            {
                throw Refuse("evidence", "invalid-settings-map");
            }
            ValidateSettingPath(manifest.SettingsPaths.Snapshots, manifest.Settings.Snapshots, requiredFile: false);
            if (!manifest.SettingsPaths.Database.Existed ||
                !manifest.Databases.Any(database => database.SafetyPath == "safety/planner.db" &&
                    manifest.Artifacts.Any(artifact => artifact.Id == database.DatabaseArtifactId &&
                        PlannerRetainedStatePaths.SamePath(artifact.OriginalPath, manifest.Settings.Database))))
            {
                throw Refuse("evidence", "invalid-working-database-map");
            }
            foreach (PlannerRetainedDirectory directory in manifest.Directories)
            {
                _ = OriginalPath(new(directory.RootId, directory.RelativePath, true));
                string rawDirectory = ("raw/" + directory.RootId + "/" + directory.RelativePath).TrimEnd('/');
                if (!directories.Contains(rawDirectory))
                {
                    throw Mismatch("evidence", "raw-directory-missing");
                }
            }
            if (catalog.Keys.Count(path => path.StartsWith("raw/", StringComparison.Ordinal)) != manifest.Artifacts.Count ||
                manifest.Databases.Count != manifest.Artifacts.Count(artifact => artifact.Kind == "database") ||
                manifest.Databases.Select(database => database.DatabaseArtifactId).Distinct(StringComparer.Ordinal).Count() != manifest.Databases.Count)
            {
                throw Refuse("evidence", "incomplete-artifact-inventory");
            }
            foreach (PlannerRetainedDatabaseInventory database in manifest.Databases)
            {
                if (!artifactIds.Contains(database.DatabaseArtifactId) || database.Checks.Any(check => check.Status == "failed") ||
                    !database.SafetyPath.StartsWith("safety/", StringComparison.Ordinal))
                {
                    throw Refuse("evidence", "incomplete-database-inventory");
                }
                RequireFile(database.SafetyPath, database.SafetySha256, database.SafetyLength);
                foreach (string suffix in PlannerRetainedStatePaths.SqliteSuffixes)
                {
                    if (files.ContainsKey(database.SafetyPath + suffix))
                    {
                        throw Refuse("evidence", "safety-copy-not-standalone");
                    }
                }
                foreach (PlannerRetainedRows rows in new[]
                         {
                             database.Schema, database.TableList, database.DatabaseMetadata,
                             database.IntegrityCheck, database.ForeignKeyCheck,
                         }.Concat(database.Tables.SelectMany(table => table.Metadata.Prepend(table.Rows))))
                {
                    RequireFile(rows.Path, rows.Sha256);
                }
            }
            (IReadOnlyList<PlannerRetainedEvidenceFile> finalFiles, IReadOnlyList<string> finalDirectories) = Catalog(bundle, ct);
            if (!finalFiles.Select(file => file.Path).Order(StringComparer.Ordinal).SequenceEqual(files.Keys.Order(StringComparer.Ordinal)) ||
                !finalDirectories.Order(StringComparer.Ordinal).SequenceEqual(directories.Order(StringComparer.Ordinal)))
            {
                throw Mismatch("evidence", "bundle-membership-mismatch");
            }
            return Task.FromResult(new PlannerRetainedVerifiedCapture(manifest, completion, handles));

            void Freeze(string directory)
            {
                handles.Add(PlannerRetainedStatePaths.FreezeDirectory(directory));
                directories.Add(PlannerRetainedStatePaths.Relative(bundle, directory));
                foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
                {
                    if (!PlannerRetainedStatePaths.TryAttributes(path, out FileAttributes attributes) ||
                        attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        throw Refuse("evidence", "reparse-or-changing-bundle");
                    }
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        Freeze(path);
                    }
                    else
                    {
                        FileStream file = PlannerRetainedStatePaths.OpenFrozenFile(path);
                        handles.Add(file);
                        files.Add(PlannerRetainedStatePaths.Relative(bundle, path), file);
                    }
                }
            }

            string OriginalPath(PlannerRetainedPath map)
            {
                if (!rootMap.TryGetValue(map.RootId, out PlannerRetainedRoot? root) ||
                    Path.IsPathRooted(map.RelativePath) || map.RelativePath.Contains(':') ||
                    map.RelativePath.Replace('\\', '/').Split('/').Any(part => part is "." or ".."))
                {
                    throw Refuse("evidence", "invalid-root-map");
                }
                string path = PlannerRetainedStatePaths.AbsoluteSyntax(Path.Combine(
                    root.OriginalDirectory, map.RelativePath.Replace('/', '\\')));
                if (!PlannerRetainedStatePaths.Contains(root.OriginalDirectory, path))
                {
                    throw Refuse("evidence", "invalid-root-map");
                }
                return path;
            }

            void ValidateSettingPath(PlannerRetainedPath map, string setting, bool requiredFile)
            {
                if (!PlannerRetainedStatePaths.SamePath(OriginalPath(map), PlannerRetainedStatePaths.AbsoluteSyntax(setting)))
                {
                    throw Refuse("evidence", "invalid-settings-map");
                }
                bool present = requiredFile
                    ? manifest.Artifacts.Any(artifact => artifact.RootId == map.RootId && artifact.RelativePath == map.RelativePath)
                    : manifest.Directories.Any(directory => directory.RootId == map.RootId && directory.RelativePath == map.RelativePath);
                if (map.Existed != present)
                {
                    throw Refuse("evidence", "invalid-settings-presence");
                }
            }

            T Read<T>(string path)
            {
                FileStream file = files[path];
                file.Position = 0;
                T value = JsonSerializer.Deserialize<T>(file, PlannerRetainedStateEvidence.Json)
                    ?? throw Refuse("evidence", "invalid-json");
                file.Position = 0;
                return value;
            }

            void RequireFile(string path, string hash, long? length = null)
            {
                _ = PlannerRetainedStatePaths.Under(bundle, path);
                if (!catalog.TryGetValue(path, out PlannerRetainedEvidenceFile? file) ||
                    file.Sha256 != hash || (length.HasValue && file.Length != length.Value))
                {
                    throw Mismatch("evidence", "catalog-reference-mismatch");
                }
            }
        }
        catch (Exception ex)
        {
            foreach (IDisposable handle in handles.AsEnumerable().Reverse())
            {
                handle.Dispose();
            }
            if (ex is JsonException or ArgumentException)
            {
                throw new PlannerRetainedStateException("evidence", "invalid-completion-metadata", inner: ex);
            }
            throw;
        }
    }

    private static void ValidateSettings(PlannerRetainedStateSettings settings)
    {
        if (settings.ProcessorKind != "jira-fhir" || settings.SettingsAuthority != "owner-attested" ||
            settings.RuntimeConfigurationIndependentlyObserved || settings.SnapshotSchemaVersion < 1 ||
            (settings.ActivateRunBackedAuthoring && settings.PreCutoverBackup.Length == 0) ||
            !PlannerRetainedStateCommand.IsObjectId(settings.CandidateCommit) ||
            !PlannerRetainedStateCommand.IsObjectId(settings.CandidateTree))
        {
            throw Refuse("arguments", "invalid-explicit-settings");
        }
    }

    private static void Recheck(
        PlannerRetainedStatePaths paths, PlannerRetainedMembership membership, IReadOnlyList<FrozenFile> frozen, CancellationToken ct)
    {
        PlannerRetainedMembership current = paths.Enumerate();
        if (!membership.Files.SequenceEqual(current.Files) || !membership.Directories.SequenceEqual(current.Directories))
        {
            throw Refuse("freeze", "membership-changed");
        }
        foreach (FrozenFile input in frozen)
        {
            ct.ThrowIfCancellationRequested();
            if (input.File.Length != input.Member.Length ||
                PlannerRetainedStatePaths.Identify(input.File.SafeFileHandle, input.Member.Path) != input.Member.Identity ||
                PlannerRetainedStateEvidence.Hash(input.File, ct) != input.Sha256)
            {
                throw Mismatch("freeze", "original-preservation-mismatch");
            }
        }
    }

    private static void VerifyRaw(string bundle, IReadOnlyList<PlannerRetainedArtifact> artifacts, CancellationToken ct)
    {
        foreach (PlannerRetainedArtifact artifact in artifacts)
        {
            PlannerRetainedEvidenceFile raw = PlannerRetainedStateEvidence.Fingerprint(
                bundle, PlannerRetainedStatePaths.Under(bundle, artifact.RawPath), ct);
            if (raw.Length != artifact.Length || raw.Sha256 != artifact.Sha256)
            {
                throw Mismatch("raw-copy", "original-raw-preservation-mismatch");
            }
        }
    }

    private static (List<PlannerRetainedArtifact>, List<PlannerRetainedSidecar>) ClassifyFamilies(
        string bundle, IReadOnlyList<PlannerRetainedArtifact> inputs, PlannerRetainedStateSettings settings)
    {
        List<PlannerRetainedArtifact> artifacts = [];
        foreach (PlannerRetainedArtifact artifact in inputs)
        {
            using FileStream file = PlannerRetainedStatePaths.OpenFrozenFile(PlannerRetainedStatePaths.Under(bundle, artifact.RawPath));
            byte[] header = new byte[100];
            int length = checked((int)Math.Min(file.Length, header.Length));
            file.ReadExactly(header.AsSpan(0, length));
            string name = Path.GetFileName(artifact.OriginalPath);
            string kind = "file";
            bool superJournal = name.Contains("-mj", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("-sj", StringComparison.OrdinalIgnoreCase);
            bool journal = name.EndsWith("-journal", StringComparison.OrdinalIgnoreCase) ||
                (length >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }));
            if ((journal || superJournal) && artifact.Length != 0)
            {
                throw Refuse("family-admission", superJournal ? "unsupported-super-journal" : "unsupported-rollback-journal");
            }
            if (length >= 16 && header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
            {
                if (length != 100 || header[18] is not (1 or 2) || header[19] is not (1 or 2))
                {
                    throw Refuse("family-admission", "unclassified-sqlite-family");
                }
                kind = "database";
            }
            else if (name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase))
            {
                if (artifact.Length != 0)
                {
                    uint magic = length >= 32 ? BinaryPrimitives.ReadUInt32BigEndian(header) : 0;
                    uint pageSize = length >= 32 ? BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)) : 0;
                    if (magic is not (0x377f0682 or 0x377f0683) ||
                        pageSize < 512 || pageSize > 65536 || (pageSize & (pageSize - 1)) != 0 ||
                        (artifact.Length - 32) % (pageSize + 24) != 0)
                    {
                        throw Refuse("family-admission", "unclassified-sqlite-family");
                    }
                }
                kind = "sqlite-wal";
            }
            else if (name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
            {
                kind = "sqlite-shm";
            }
            else if (journal || superJournal)
            {
                kind = superJournal ? "empty-super-journal" : "sqlite-journal";
            }
            else if (PlannerRetainedStatePaths.SamePath(artifact.OriginalPath, settings.Database) ||
                     (settings.PreCutoverBackup.Length != 0 &&
                         PlannerRetainedStatePaths.SamePath(artifact.OriginalPath, settings.PreCutoverBackup)) ||
                     name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".db.tmp", StringComparison.OrdinalIgnoreCase) ||
                     (length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(header) is 0x377f0682 or 0x377f0683))
            {
                throw Refuse("family-admission", "unclassified-sqlite-family");
            }
            artifacts.Add(artifact with { Kind = kind });
        }
        List<PlannerRetainedSidecar> sidecars = [];
        foreach (PlannerRetainedArtifact database in artifacts.Where(artifact => artifact.Kind == "database"))
        {
            foreach (string suffix in PlannerRetainedStatePaths.SqliteSuffixes)
            {
                PlannerRetainedArtifact? sidecar = artifacts.SingleOrDefault(artifact =>
                    PlannerRetainedStatePaths.SamePath(artifact.OriginalPath, database.OriginalPath + suffix));
                if (sidecar is not null && sidecar.Kind != "sqlite" + suffix)
                {
                    throw Refuse("family-admission", "unclassified-sqlite-family");
                }
                sidecars.Add(new(database.Id, suffix, sidecar?.Id));
            }
        }
        if (artifacts.Any(artifact => artifact.Kind.StartsWith("sqlite-", StringComparison.Ordinal) &&
                !sidecars.Any(sidecar => sidecar.ArtifactId == artifact.Id)) ||
            !artifacts.Any(artifact => artifact.Kind == "database" &&
                PlannerRetainedStatePaths.SamePath(artifact.OriginalPath, settings.Database)))
        {
            throw Refuse("family-admission", "unclassified-sqlite-family");
        }
        return (artifacts, sidecars);
    }

    private static SqliteConnection OpenDisposable(string path)
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

    private static void RequireStandalone(PlannerRetainedStatePaths paths, string relative)
    {
        string path = paths.InBundle(relative);
        foreach (string suffix in PlannerRetainedStatePaths.SqliteSuffixes)
        {
            if (PlannerRetainedStatePaths.TryAttributes(path + suffix, out _))
            {
                throw Refuse("normalization", "safety-copy-not-standalone");
            }
        }
        using FileStream file = PlannerRetainedStatePaths.OpenFrozenFile(path);
        Span<byte> header = stackalloc byte[100];
        file.ReadExactly(header);
        if (!header[..16].SequenceEqual("SQLite format 3\0"u8))
        {
            throw Refuse("normalization", "invalid-safety-copy");
        }
    }

    private static void CopyFile(string source, string destination, CancellationToken ct)
    {
        using FileStream input = PlannerRetainedStatePaths.OpenFrozenFile(source);
        Copy(input, destination, ct);
    }

    private static void Copy(Stream source, string destination, CancellationToken ct)
    {
        source.Position = 0;
        using FileStream output = PlannerRetainedStatePaths.CreateFile(destination);
        byte[] buffer = new byte[81920];
        int length;
        while ((length = source.Read(buffer)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, length);
        }
        output.Flush(flushToDisk: true);
        source.Position = 0;
    }

    private static (IReadOnlyList<PlannerRetainedEvidenceFile>, IReadOnlyList<string>) Catalog(string bundle, CancellationToken ct)
    {
        List<PlannerRetainedEvidenceFile> files = [];
        List<string> directories = [];
        Walk(bundle);
        return (files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(),
            directories.Order(StringComparer.Ordinal).ToArray());

        void Walk(string directory)
        {
            using var pin = PlannerRetainedStatePaths.FreezeDirectory(directory);
            directories.Add(PlannerRetainedStatePaths.Relative(bundle, directory));
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                if (!PlannerRetainedStatePaths.TryAttributes(path, out FileAttributes attributes) ||
                    attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw Refuse("completion", "changing-evidence");
                }
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    Walk(path);
                }
                else
                {
                    files.Add(PlannerRetainedStateEvidence.Fingerprint(bundle, path, ct));
                }
            }
        }
    }

    private static async Task<(List<PlannerRetainedSnapshotReference>, HashSet<string>)> ValidateSnapshotClosureAsync(
        PlannerRetainedStatePaths paths, IReadOnlyList<PlannerRetainedArtifact> artifacts,
        IReadOnlyList<PlannerRetainedSidecar> sidecars, IReadOnlyList<SafetyCopy> safetyCopies, CancellationToken ct)
    {
        Dictionary<string, PlannerRetainedArtifact> byPath = artifacts.ToDictionary(
            artifact => artifact.OriginalPath, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> validationCopies = new(StringComparer.Ordinal);
        Dictionary<string, List<(PlannerRetainedArtifact Artifact, AuthoringSnapshotDescriptor Descriptor)>> descriptors = new(StringComparer.Ordinal);
        List<PlannerRetainedSnapshotReference> references = [];
        HashSet<string> verified = [];
        HashSet<string> projections = [];

        foreach (PlannerRetainedArtifact artifact in artifacts.Where(artifact =>
                     artifact.RelativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            bool namedDescriptor = artifact.RelativePath.EndsWith("descriptor.json", StringComparison.OrdinalIgnoreCase);
            // Descriptor metadata is bounded; opaque files are still captured fully.
            // A larger potential JSON descriptor is ineligible, never silently skipped.
            if (artifact.Length > 16 * 1024 * 1024)
            {
                throw Refuse("reference-closure", "unsupported-descriptor-reader");
            }
            using FileStream file = PlannerRetainedStatePaths.OpenFrozenFile(paths.InBundle(artifact.RawPath));
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(file);
            }
            catch (JsonException) when (!namedDescriptor)
            {
                continue;
            }
            using (document)
            {
                bool looksLikeDescriptor = document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("snapshotId", out _) &&
                    document.RootElement.TryGetProperty("fileName", out _);
                if (!namedDescriptor && !looksLikeDescriptor)
                {
                    continue;
                }
                AuthoringSnapshotDescriptor descriptor = document.RootElement.Deserialize<AuthoringSnapshotDescriptor>(
                    PlannerRetainedStateEvidence.Json) ?? throw Refuse("reference-closure", "invalid-descriptor");
                if (descriptor.ProcessorKind != "jira-fhir" || string.IsNullOrEmpty(descriptor.FileName) ||
                    descriptor.FileName.IndexOfAny(['/', '\\', ':']) >= 0)
                {
                    throw Refuse("reference-closure", "invalid-descriptor-owner-or-path");
                }
                string original = PlannerRetainedStatePaths.Absolute(
                    Path.Combine(Path.GetDirectoryName(artifact.OriginalPath)!, descriptor.FileName));
                if (!byPath.TryGetValue(original, out PlannerRetainedArtifact? database))
                {
                    throw Refuse("reference-closure", "descriptor-artifact-missing");
                }
                AuthoringReviewSnapshotRecord record = new()
                {
                    Id = descriptor.SnapshotId, ProcessorKind = descriptor.ProcessorKind, RunId = descriptor.RunId,
                    AuthoringEpoch = descriptor.AuthoringEpoch, Sequence = descriptor.Sequence,
                    SchemaVersion = descriptor.SchemaVersion, Status = "ready", Path = original, TempPath = original + ".tmp",
                    ChecksumSha256 = descriptor.Sha256, SizeBytes = descriptor.SizeBytes,
                    ItemCount = descriptor.ItemCount, ReceiptCount = descriptor.ReceiptCount,
                    TableCountsJson = JsonSerializer.Serialize(descriptor.TableCounts), CreatedAt = descriptor.CreatedAt,
                    PublicationProofJson = descriptor.PublicationProof is null ? null :
                        JsonSerializer.Serialize(descriptor.PublicationProof, PlannerRetainedStateEvidence.Json),
                };
                await ValidateFile(record, database);
                if (!descriptors.TryGetValue(database.Id, out var list))
                {
                    list = [];
                    descriptors.Add(database.Id, list);
                }
                list.Add((artifact, descriptor));
                references.Add(new("", record.Id, "descriptor", database.Id, null, false, [artifact.Id]));
            }
        }

        foreach (SafetyCopy safety in safetyCopies)
        {
            await using SqliteConnection connection = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(paths.InBundle(safety.Path), ct);
            if (PlannerRetainedStateInventory.TableExists(connection, "authoring_snapshot_provenance"))
            {
                projections.Add(safety.ArtifactId);
            }
            if (!PlannerRetainedStateInventory.TableExists(connection, "authoring_review_snapshots") ||
                PlannerRetainedStateInventory.Scalar(connection, "SELECT COUNT(*) FROM authoring_review_snapshots") == 0)
            {
                continue;
            }
            string[] fields =
            [
                "Id", "ProcessorKind", "RunId", "AuthoringEpoch", "Sequence", "SchemaVersion",
                "Status", "TempPath", "Path", "ChecksumSha256", "SizeBytes", "ItemCount", "ReceiptCount",
                "TableCountsJson", "CreatedAt",
            ];
            List<PlannerRetainedColumn> columns = PlannerRetainedStateInventory.ReadColumns(connection, "authoring_review_snapshots");
            if (fields.Any(field => !columns.Any(column => column.Name == field)))
            {
                throw Refuse("reference-closure", "unsupported-snapshot-reader");
            }
            bool hasProof = columns.Any(column => column.Name == "PublicationProofJson");
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", fields.Select(PlannerRetainedStateInventory.Quote))}, " +
                (hasProof ? "PublicationProofJson" : "NULL") + " FROM authoring_review_snapshots ORDER BY Id";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                foreach (int index in new[] { 0, 1, 2, 6, 7, 8, 13, 14 })
                {
                    if (reader.IsDBNull(index) || reader.GetFieldType(index) != typeof(string))
                    {
                        throw Refuse("reference-closure", "invalid-snapshot-storage-class");
                    }
                }
                foreach (int index in new[] { 3, 4, 5, 10, 11, 12 })
                {
                    if (reader.IsDBNull(index) || reader.GetFieldType(index) != typeof(long))
                    {
                        throw Refuse("reference-closure", "invalid-snapshot-storage-class");
                    }
                }
                AuthoringReviewSnapshotRecord record = new()
                {
                    Id = reader.GetString(0), ProcessorKind = reader.GetString(1), RunId = reader.GetString(2),
                    AuthoringEpoch = reader.GetInt64(3), Sequence = reader.GetInt64(4), SchemaVersion = reader.GetInt32(5),
                    Status = reader.GetString(6), TempPath = reader.GetString(7), Path = reader.GetString(8),
                    ChecksumSha256 = reader.IsDBNull(9) ? null : reader.GetString(9), SizeBytes = reader.GetInt64(10),
                    ItemCount = reader.GetInt32(11), ReceiptCount = reader.GetInt32(12),
                    TableCountsJson = reader.GetString(13),
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    PublicationProofJson = reader.IsDBNull(15) ? null : reader.GetString(15),
                };
                if (record.ProcessorKind != "jira-fhir" || record.Path == record.TempPath)
                {
                    throw Refuse("reference-closure", "wrong-processor-kind-or-snapshot-path");
                }
                // Mapping checks strings only. A stored path is never opened or followed.
                _ = paths.Map(record.Path, false);
                _ = paths.Map(record.TempPath, false);
                byPath.TryGetValue(PlannerRetainedStatePaths.Absolute(record.Path), out PlannerRetainedArtifact? final);
                byPath.TryGetValue(PlannerRetainedStatePaths.Absolute(record.TempPath), out PlannerRetainedArtifact? temporary);
                bool finalized = record.Status is "ready" or "promoted";
                if ((finalized && final is null) || (final is null && temporary is null) ||
                    record.Status is not ("ready" or "promoted" or "creating" or "error"))
                {
                    throw Refuse("reference-closure", "required-snapshot-artifact-missing");
                }
                foreach (PlannerRetainedArtifact artifact in new[] { final, temporary }.OfType<PlannerRetainedArtifact>())
                {
                    await ValidateFile(record, artifact);
                }
                List<string> matchedDescriptors = [];
                if (final is not null && descriptors.TryGetValue(final.Id, out var existing))
                {
                    foreach ((PlannerRetainedArtifact descriptorFile, AuthoringSnapshotDescriptor descriptor) in existing)
                    {
                        IReadOnlyDictionary<string, long> expectedCounts = SqliteReviewSnapshotValidator.ReadTableCounts(record.TableCountsJson);
                        AuthoringSnapshotPublicationProof? proof = record.PublicationProofJson is null ? null :
                            JsonSerializer.Deserialize<AuthoringSnapshotPublicationProof>(record.PublicationProofJson, PlannerRetainedStateEvidence.Json);
                        if (descriptor.SnapshotId != record.Id || descriptor.RunId != record.RunId ||
                            descriptor.AuthoringEpoch != record.AuthoringEpoch || descriptor.Sequence != record.Sequence ||
                            descriptor.SchemaVersion != record.SchemaVersion || descriptor.Sha256 != record.ChecksumSha256 ||
                            descriptor.SizeBytes != record.SizeBytes || descriptor.ItemCount != record.ItemCount ||
                            descriptor.ReceiptCount != record.ReceiptCount || descriptor.CreatedAt != record.CreatedAt ||
                            descriptor.PublicationProof != proof || descriptor.TableCounts.Count != expectedCounts.Count ||
                            expectedCounts.Any(pair => !descriptor.TableCounts.TryGetValue(pair.Key, out long count) || pair.Value != count))
                        {
                            throw Refuse("reference-closure", "snapshot-descriptor-mismatch");
                        }
                        matchedDescriptors.Add(descriptorFile.Id);
                    }
                }
                references.Add(new(safety.ArtifactId, record.Id, record.Status, final?.Id, temporary?.Id,
                    finalized && temporary is null, matchedDescriptors));
            }
        }
        if (!projections.IsSubsetOf(verified))
        {
            throw Refuse("reference-closure", "snapshot-without-retained-record-or-descriptor");
        }
        return (references, verified);

        async Task ValidateFile(AuthoringReviewSnapshotRecord record, PlannerRetainedArtifact artifact)
        {
            if (record.SchemaVersion != PlannedTicketSnapshotSchemaV1.Version)
            {
                throw Refuse("reference-closure", "unsupported-snapshot-schema");
            }
            if (artifact.Kind != "database" ||
                sidecars.Any(sidecar => sidecar.DatabaseArtifactId == artifact.Id && sidecar.ArtifactId is not null))
            {
                throw Refuse("reference-closure", "snapshot-not-self-contained");
            }
            if (!validationCopies.TryGetValue(artifact.Id, out string? copy))
            {
                copy = paths.Output("normalization/validation/" + artifact.Id + ".db");
                CopyFile(paths.InBundle(artifact.RawPath), copy, ct);
                validationCopies.Add(artifact.Id, copy);
            }
            SqliteReviewSnapshotValidationResult result = await SqliteReviewSnapshotValidator.ValidateAsync(record, copy, ct: ct);
            if (!result.IsValid)
            {
                throw Refuse("reference-closure", "snapshot-checksum-count-or-provenance-mismatch");
            }
            await using SqliteConnection snapshot = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(copy, ct);
            foreach (AuthoringSnapshotTableSchema table in PlannedTicketSnapshotSchemaV1.Tables)
            {
                List<PlannerRetainedColumn> columns = PlannerRetainedStateInventory.ReadColumns(snapshot, table.Name);
                if (!columns.Select(column => column.Name).Order(StringComparer.Ordinal)
                        .SequenceEqual(table.Columns.Order(StringComparer.Ordinal)))
                {
                    throw Refuse("reference-closure", "unsupported-snapshot-schema");
                }
            }
            // Snapshot provenance item/receipt counts are the selected run's coordinates,
            // not the size of the projected cross-run historical corpus.
            using SqliteCommand counts = snapshot.CreateCommand();
            counts.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM authoring_run_items WHERE RunId = @run),
                    (SELECT COUNT(*) FROM authoring_result_receipts WHERE RunId = @run)
                """;
            counts.Parameters.AddWithValue("@run", record.RunId);
            using SqliteDataReader countReader = counts.ExecuteReader();
            if (!countReader.Read() || countReader.GetInt64(0) != record.ItemCount ||
                countReader.GetInt64(1) != record.ReceiptCount)
            {
                throw Refuse("reference-closure", "snapshot-membership-count-mismatch");
            }
            verified.Add(artifact.Id);
        }
    }

    private static PlannerRetainedStateException Refuse(string stage, string category) => new(stage, category);
    private static PlannerRetainedStateException Mismatch(string stage, string category) => new(stage, category, 1);
}
