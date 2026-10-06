using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Maintenance;

internal sealed record PlannerRetainedStateSettings(
    string Database,
    string PreCutoverBackup,
    string Snapshots,
    bool ActivateRunBackedAuthoring,
    int SnapshotSchemaVersion,
    bool StartProcessingOnStartup,
    bool ReconcileSnapshotsOnStartup,
    bool HydrationBackfillOnStartup,
    string Bundle,
    string CandidateCommit,
    string CandidateTree,
    IReadOnlyList<string> RetainedRoots)
{
    public string ProcessorKind { get; init; } = "jira-fhir";
    public string SettingsAuthority { get; init; } = "owner-attested";
    public bool RuntimeConfigurationIndependentlyObserved { get; init; }
}

internal sealed record PlannerRetainedFileIdentity(ulong Volume, string FileId, uint LinkCount);

internal sealed record PlannerRetainedRoot(
    string Id, string OriginalDirectory, string Scope, string? SelectedFile, bool Existed);

internal sealed record PlannerRetainedPath(string RootId, string RelativePath, bool Existed);

internal sealed record PlannerRetainedSettingsPaths(
    PlannerRetainedPath Database, PlannerRetainedPath? PreCutoverBackup, PlannerRetainedPath Snapshots);

internal sealed record PlannerRetainedDirectory(
    string RootId, string RelativePath, PlannerRetainedFileIdentity Identity);

internal sealed record PlannerRetainedArtifact(
    string Id, string RootId, string RelativePath, string OriginalPath, string RawPath,
    PlannerRetainedFileIdentity Identity, long Length, string Sha256, string Kind);

internal sealed record PlannerRetainedSidecar(string DatabaseArtifactId, string Suffix, string? ArtifactId);

internal sealed record PlannerRetainedOwnerLock(
    bool Preexisted, bool CreatedByCapture, long Length, string? Sha256,
    PlannerRetainedFileIdentity? PreviousIdentity);

internal sealed record PlannerRetainedEvidenceFile(string Path, long Length, string Sha256);

internal sealed record PlannerRetainedBinary(
    string Anchor, string Path, long Length, string Sha256);

internal sealed record PlannerRetainedColumn(
    int Ordinal, string Name, string DeclaredType, bool NotNull, string? DefaultSql, int PrimaryKeyOrder, int Hidden);

internal sealed record PlannerRetainedRows(
    string Path, long RowCount, string Sha256, IReadOnlyList<string> Columns);

internal sealed record PlannerRetainedTable(
    string Name, string Kind, bool WithoutRowId, bool Strict, string? RowIdExpression,
    IReadOnlyList<PlannerRetainedColumn> Columns, PlannerRetainedRows Rows,
    IReadOnlyList<PlannerRetainedRows> Metadata);

internal sealed record PlannerRetainedCheck(string Name, string Status, long Violations);

internal sealed record PlannerRetainedSnapshotReference(
    string DatabaseArtifactId, string SnapshotId, string Status,
    string? FinalArtifactId, string? TempArtifactId, bool FinalizedTempAbsent,
    IReadOnlyList<string> DescriptorArtifactIds);

internal sealed record PlannerRetainedDatabaseInventory(
    string DatabaseArtifactId, string SafetyPath, string SafetySha256, long SafetyLength,
    PlannerRetainedRows Schema, PlannerRetainedRows TableList, PlannerRetainedRows DatabaseMetadata,
    PlannerRetainedRows IntegrityCheck, PlannerRetainedRows ForeignKeyCheck,
    IReadOnlyList<PlannerRetainedTable> Tables,
    IReadOnlyList<string> AbsentLegacyTables, IReadOnlyList<PlannerRetainedCheck> Checks);

internal sealed record PlannerRetainedStateManifest(
    int FormatVersion, string Status, DateTimeOffset CapturedAt,
    string CandidateCommit, string CandidateTree, string CandidateAuthority,
    PlannerRetainedStateSettings Settings, IReadOnlyList<PlannerRetainedRoot> Roots,
    PlannerRetainedSettingsPaths SettingsPaths, IReadOnlyList<PlannerRetainedDirectory> Directories,
    PlannerRetainedOwnerLock OwnerLock, IReadOnlyList<PlannerRetainedArtifact> Artifacts,
    IReadOnlyList<PlannerRetainedSidecar> Sidecars,
    IReadOnlyList<PlannerRetainedDatabaseInventory> Databases,
    IReadOnlyList<PlannerRetainedSnapshotReference> SnapshotReferences,
    IReadOnlyList<PlannerRetainedBinary> Binaries);

internal sealed record PlannerRetainedStateCompletion(
    int FormatVersion, string Status, string CandidateCommit, string CandidateTree,
    string ManifestSha256, IReadOnlyList<PlannerRetainedEvidenceFile> Files,
    IReadOnlyList<string> Directories, IReadOnlyList<PlannerRetainedBinary> Binaries);

internal sealed record PlannerRetainedRehearsalRequest(
    string Bundle, string Output, string CandidateCommit, string CandidateTree);

internal sealed record PlannerRetainedRehearsalMap(
    string Database, string? PreCutoverBackup, string Snapshots, string Temp,
    IReadOnlyList<PlannerRetainedRehearsalArtifact> Artifacts);

internal sealed record PlannerRetainedRehearsalArtifact(
    string ArtifactId, string Path, string Disposition, long Length, string Sha256);

internal sealed record PlannerRetainedRehearsalDelta(
    string Table, string Rule, string Status, long BeforeRows, long AfterRows,
    PlannerRetainedRows? Before, PlannerRetainedRows? After, PlannerRetainedRows? Expected);

internal sealed record PlannerRetainedRehearsalCheckpoint(
    string Name, string Status, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    string? Previous, PlannerRetainedDatabaseInventory Inventory,
    IReadOnlyList<PlannerRetainedRehearsalDelta> Deltas, string DeltaDetails);

internal sealed record PlannerRetainedRehearsalStage(
    int Pass, string Name, string Status, string? Checkpoint, string? Category);

internal sealed record PlannerRetainedRehearsalGuard(
    string Name, string Source, string BeforeCheckpoint, string FailureCheckpoint,
    string Facts, PlannerRetainedEvidenceFile? PreservedBackup,
    string? BackupMode, PlannerRetainedEvidenceFile? RegeneratedCandidate,
    string? CandidateAuthority);

internal sealed record PlannerRetainedRehearsalPreservation(
    string CaptureOriginals, string Bundle, string CopiedArtifacts,
    IReadOnlyList<PlannerRetainedRehearsalArtifact> Artifacts,
    IReadOnlyList<PlannerRetainedEvidenceFile> NewWorkFiles);

internal sealed record PlannerRetainedRehearsalResult(
    int FormatVersion, string Status, string EvidenceStatus, string StartupOutcome, string? GuardName,
    string CandidateCommit, string CandidateTree, string CaptureManifestSha256,
    IReadOnlyList<PlannerRetainedBinary> Binaries, PlannerRetainedStateSettings CapturedSettings,
    PlannerRetainedRehearsalMap PathMap, PlannerRetainedDatabaseInventory Baseline,
    IReadOnlyList<PlannerRetainedRehearsalStage> Stages,
    IReadOnlyList<PlannerRetainedRehearsalCheckpoint> Checkpoints,
    PlannerRetainedRehearsalGuard? Guard, PlannerRetainedRehearsalPreservation Preservation,
    IReadOnlyList<PlannerRetainedEvidenceFile> EvidenceFiles, string? FailureCategory,
    string? FailureDetail)
{
    [JsonIgnore]
    internal int ExitCode => (Status, EvidenceStatus, StartupOutcome, GuardName) switch
    {
        ("passed", "complete", "passed", null) when Guard is null => 0,
        ("blocked", "complete", "guarded-refusal",
            "schema-admission-refused" or "backup-hash-mismatch" or "backup-missing-or-invalid" or
            "missing-revalidation-source" or "unknown-processor-mode") when Guard?.Name == GuardName => 20,
        _ => 1,
    };
}

internal sealed class PlannerRetainedVerifiedCapture(
    PlannerRetainedStateManifest manifest, PlannerRetainedStateCompletion completion, List<IDisposable> handles)
    : IDisposable
{
    internal PlannerRetainedStateManifest Manifest { get; } = manifest;
    internal PlannerRetainedStateCompletion Completion { get; } = completion;

    public void Dispose()
    {
        foreach (IDisposable handle in handles.AsEnumerable().Reverse())
        {
            handle.Dispose();
        }
        handles.Clear();
    }
}

internal sealed class PlannerRetainedStateException(
    string stage, string category, int exitCode = 20, Exception? inner = null)
    : InvalidOperationException($"{stage}: {category}", inner)
{
    internal string Stage { get; } = stage;
    internal string Category { get; } = category;
    internal int ExitCode { get; } = exitCode;
}

internal static class PlannerRetainedStateEvidence
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    internal static void WriteJson<T>(string path, T value)
    {
        using FileStream file = PlannerRetainedStatePaths.CreateFile(path);
        JsonSerializer.Serialize(file, value, Json);
        file.Flush(flushToDisk: true);
    }

    internal static T ReadJson<T>(string path)
    {
        using FileStream file = PlannerRetainedStatePaths.OpenFrozenFile(path);
        return JsonSerializer.Deserialize<T>(file, Json)
            ?? throw new PlannerRetainedStateException("evidence", "invalid-json");
    }

    internal static string Hash(Stream stream, CancellationToken ct = default)
    {
        stream.Position = 0;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        int length;
        while ((length = stream.Read(buffer)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, length);
        }
        stream.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static PlannerRetainedEvidenceFile Fingerprint(string root, string path, CancellationToken ct = default)
    {
        using FileStream file = PlannerRetainedStatePaths.OpenFrozenFile(path);
        return new(PlannerRetainedStatePaths.Relative(root, path), file.Length, Hash(file, ct));
    }

    // Anchors are computed by the executing candidate, never taken from a manifest's original paths.
    internal static IReadOnlyList<PlannerRetainedBinary> FingerprintBinaries(CancellationToken ct = default)
    {
        string application = Path.GetDirectoryName(typeof(PlannerRetainedStateCommand).Assembly.Location)
            ?? throw new PlannerRetainedStateException("provenance", "assembly-location-unavailable");
        Dictionary<string, string> anchors = new(StringComparer.Ordinal)
        {
            ["application"] = application,
            ["runtime"] = Path.GetDirectoryName(typeof(object).Assembly.Location)!,
            ["aspnet-runtime"] = Path.GetDirectoryName(typeof(Microsoft.AspNetCore.Builder.WebApplication).Assembly.Location)!,
        };
        string assemblyName = Path.GetFileNameWithoutExtension(typeof(PlannerRetainedStateCommand).Assembly.Location);
        foreach (string extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            if (!File.Exists(Path.Combine(application, assemblyName + extension)))
            {
                throw new PlannerRetainedStateException("provenance", "candidate-binary-contract-missing");
            }
        }

        List<PlannerRetainedBinary> result = [];
        foreach ((string anchor, string directory) in anchors)
        {
            IEnumerable<string> paths = Directory.EnumerateFiles(directory)
                .Where(IsBinary);
            string nativeDirectory = Path.Combine(directory, "runtimes", "win-x64", "native");
            if (anchor == "application" && Directory.Exists(nativeDirectory))
            {
                paths = paths.Concat(Directory.EnumerateFiles(nativeDirectory).Where(IsBinary));
            }
            foreach (string path in paths.Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                PlannerRetainedEvidenceFile file = Fingerprint(directory, path, ct);
                result.Add(new(anchor, file.Path, file.Length, file.Sha256));
            }
        }
        return result;

        static bool IsBinary(string path) =>
            path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SameBinaries(
        IReadOnlyList<PlannerRetainedBinary> left, IReadOnlyList<PlannerRetainedBinary> right)
        => left.SequenceEqual(right);
}
