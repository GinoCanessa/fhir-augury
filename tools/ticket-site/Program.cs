using System.Security.Cryptography;
using FhirAugury.Common.IO;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Tools.TicketSite;

internal sealed record TicketSiteCleanupHooks(
    Func<ImmutableFileSnapshot, ValueTask>? DisposeSnapshotAsync = null,
    Func<string, ValueTask>? DeleteFilteredDatabaseAsync = null);

public static class Program
{
    private const string DefaultTitle = "Ticket Site";
    private const string DefaultOutSubpath = "cache/jira-ticket-site";

    public static Task<int> Main(string[] args) => RunAsync(args);

    internal static async Task<int> RunAsync(
        string[] args,
        TicketSiteCleanupHooks? cleanupHooks = null)
    {
        if (!TryParseArgs(args, out CliOptions options, out string? parseError))
        {
            await Console.Error.WriteLineAsync(parseError).ConfigureAwait(false);
            WriteUsage(Console.Error);
            return 2;
        }

        if (options.Help)
        {
            WriteUsage(Console.Out);
            return 0;
        }

        int inputCount =
            (options.PreparerSnapshotSupplied ? 1 : 0) +
            (options.PlannerSnapshotSupplied ? 1 : 0);
        if (inputCount != 1)
        {
            await Console.Error.WriteLineAsync(
                "Specify either --preparer-snapshot or --planner-snapshot (exactly one).")
                .ConfigureAwait(false);
            return 2;
        }
        if (string.IsNullOrWhiteSpace(options.SnapshotDescriptorPath))
        {
            await Console.Error.WriteLineAsync(
                "Snapshot mode requires --snapshot-descriptor <path>.").ConfigureAwait(false);
            return 2;
        }

        string kind;
        string subSiteFolder;
        string dbPath;
        if (options.PreparerSnapshotSupplied)
        {
            kind = PreparerSubSiteEmitter.Kind;
            subSiteFolder = PreparerSubSiteEmitter.SubSiteFolder;
            dbPath = options.PreparerSnapshotPath!;
        }
        else
        {
            kind = PlannerSubSiteEmitter.Kind;
            subSiteFolder = PlannerSubSiteEmitter.SubSiteFolder;
            dbPath = options.PlannerSnapshotPath!;
        }

        string resolvedDb = Path.GetFullPath(dbPath);
        string rootOut = Path.GetFullPath(options.OutPath ?? Path.Combine(Directory.GetCurrentDirectory(), DefaultOutSubpath));
        string subSiteOut = Path.Combine(rootOut, subSiteFolder);
        string title = options.Title;

        if (!File.Exists(resolvedDb))
        {
            await Console.Error.WriteLineAsync($"Database file not found: {resolvedDb}").ConfigureAwait(false);
            return 1;
        }

        int exit = await EmitSnapshotSubSiteAsync(
            options,
            kind,
            resolvedDb,
            subSiteOut,
            title).ConfigureAwait(false);
        if (exit != 0) return exit;

        // Always regenerate the chooser after a sub-site emit. It scans the
        // root dir for which sub-sites exist; no marker file of its own.
        ChooserPageEmitter.Emit(rootOut);
        return 0;

        async Task<int> EmitSnapshotSubSiteAsync(
            CliOptions opts,
            string siteKind,
            string db,
            string subOut,
            string siteTitle)
        {
            ImmutableFileSnapshot? privateSnapshot = null;
            string? filteredDatabasePath = null;
            bool ownsFilteredDatabase = false;
            SiteBuildManifest? successSummary = null;
            try
            {
                string descriptorPath = Path.GetFullPath(opts.SnapshotDescriptorPath!);
                StagedDirectoryPublisher.RejectSourcePathsWithinPublication(
                    rootOut,
                    db,
                    descriptorPath);
                StagedDirectoryPublisher.RejectSourcePathsWithinPublication(
                    subOut,
                    db,
                    descriptorPath);

                ImmutableFileSnapshot snapshot =
                    await ImmutableFileSnapshot.CreateAsync(
                        db,
                        cleanupWarning: message =>
                            Console.Error.WriteLine($"Warning: {message}"))
                        .ConfigureAwait(false);
                privateSnapshot = snapshot;
                HydrationAssertion.SnapshotValidationResult? validation =
                    await HydrationAssertion.ValidateSnapshotAsync(
                        snapshot,
                        descriptorPath,
                        siteKind,
                        Console.Error,
                        CancellationToken.None).ConfigureAwait(false);
                if (validation is null)
                {
                    return 1;
                }

                ResolvedFilters? filters = await FilterResolver.TryResolveAsync(
                    snapshot.Path,
                    opts,
                    siteKind,
                    Console.Error,
                    CancellationToken.None).ConfigureAwait(false);
                if (filters is null)
                {
                    return 1;
                }

                EchoResolvedFilter("--spec", opts.FilterSpec, filters.Specification);
                EchoResolvedFilter("--project", opts.FilterProject, filters.Project);
                EchoResolvedFilter("--wg", opts.FilterWorkGroup, filters.WorkGroup);

                string embeddedDbPath;
                long survivingCount;
                if (siteKind == PreparerSubSiteEmitter.Kind)
                {
                    PreparerDbTrimmer.BuildResult built =
                        await PreparerDbTrimmer.BuildAsync(
                            snapshot.Path,
                            filters,
                            immutableSnapshot: true,
                            CancellationToken.None).ConfigureAwait(false);
                    embeddedDbPath = built.TempDbPath;
                    filteredDatabasePath = built.TempDbPath;
                    survivingCount = built.SurvivingTicketCount;
                    ownsFilteredDatabase = built.OwnsTempFile;
                }
                else
                {
                    PlannerDbTrimmer.BuildResult built =
                        await PlannerDbTrimmer.BuildAsync(
                            snapshot.Path,
                            filters,
                            immutableSnapshot: true,
                            CancellationToken.None).ConfigureAwait(false);
                    embeddedDbPath = built.TempDbPath;
                    filteredDatabasePath = built.TempDbPath;
                    survivingCount = built.SurvivingTicketCount;
                    ownsFilteredDatabase = built.OwnsTempFile;
                }

                byte[] dbBytes =
                    await ReadAllBytesWithTransientRetryAsync(embeddedDbPath)
                        .ConfigureAwait(false);
                string embeddedSha256 = ComputeSha256(dbBytes);
                IReadOnlyDictionary<string, long> manifestCounts =
                    await HydrationAssertion.ReadManifestCountsAsync(
                        embeddedDbPath,
                        siteKind,
                        CancellationToken.None).ConfigureAwait(false);
                DateTimeOffset generatedAt = DateTimeOffset.UtcNow;
                string rendererAssetsVersion = siteKind == PreparerSubSiteEmitter.Kind
                    ? PreparerSubSiteEmitter.RendererAssetsVersion
                    : PlannerSubSiteEmitter.RendererAssetsVersion;
                SiteBuildManifest manifest = SiteBuildManifest.Create(
                    siteKind,
                    validation.Descriptor,
                    checked((int)survivingCount),
                    manifestCounts,
                    filters,
                    siteTitle,
                    rendererAssetsVersion,
                    embeddedSha256,
                    dbBytes.LongLength,
                    subOut,
                    generatedAt);

                StagedDirectoryPublishResult result =
                    await StagedDirectoryPublisher.PublishAsync(
                        subOut,
                        StagedDirectoryVersion.Snapshot(
                            TicketSiteOwner(siteKind),
                            validation.Descriptor.ProcessorKind,
                            validation.Descriptor.Sequence,
                            validation.Descriptor.SnapshotId,
                            manifest.BuildIdentity),
                        (staging, _) =>
                        {
                            if (siteKind == PreparerSubSiteEmitter.Kind)
                            {
                                PreparerSubSiteEmitter.Emit(
                                    staging,
                                    siteTitle,
                                    filters,
                                    dbBytes,
                                    snapshotMode: true);
                            }
                            else
                            {
                                PlannerSubSiteEmitter.Emit(
                                    staging,
                                    siteTitle,
                                    filters,
                                    dbBytes);
                            }
                            OutputDirGuard.WriteMarker(
                                staging,
                                siteKind,
                                filters,
                                generatedAt,
                                validation.Descriptor);
                            SiteBuildManifest.Write(staging, manifest);
                            return Task.CompletedTask;
                        },
                        (staging, ct) => OutputDirGuard.ValidateSnapshotStageAsync(
                            staging,
                            siteKind,
                            manifest,
                            ct),
                        new StagedDirectoryPublishOptions(Force: opts.Force),
                        CancellationToken.None).ConfigureAwait(false);

                successSummary = result.Outcome ==
                    StagedDirectoryPublishOutcome.Idempotent
                    ? SiteBuildManifest.Read(
                        Path.Combine(subOut, SiteBuildManifest.FileName))
                    : manifest;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or
                InvalidOperationException)
            {
                await Console.Error.WriteLineAsync(
                    $"Snapshot site publication failed: {ex.Message}")
                    .ConfigureAwait(false);
                return 1;
            }
            finally
            {
                if (ownsFilteredDatabase && filteredDatabasePath is not null)
                {
                    await CleanupFilteredDatabaseBestEffortAsync(
                        filteredDatabasePath,
                        cleanupHooks).ConfigureAwait(false);
                }
                if (privateSnapshot is not null)
                {
                    await CleanupSnapshotBestEffortAsync(
                        privateSnapshot,
                        cleanupHooks).ConfigureAwait(false);
                }
            }

            Console.WriteLine(SiteBuildManifest.ToSummaryJson(successSummary!));
            return 0;
        }

    }

    private static void EchoResolvedFilter(string flag, string? raw, string? canonical)
    {
        if (raw is null || canonical is null) return;
        if (!string.Equals(raw, canonical, StringComparison.Ordinal))
        {
            Console.WriteLine($"Resolved {flag} '{raw}' → '{canonical}'.");
        }
    }

    // Read the just-trimmed temp DB tolerating a transient Windows sharing
    // violation. With Pooling=false on the temp-DB SqliteConnection (see
    // PreparerDbTrimmer / PlannerDbTrimmer), the
    // native file handle is released synchronously on Dispose; AV scanners
    // and the OS file-cache flush can then briefly hold the freshly
    // released file in a way that races a vanilla File.ReadAllBytesAsync.
    //
    // Approach:
    //   1. Open with FileShare.ReadWrite | Delete so any concurrent reader
    //      (AV scanner) that uses FileShare.Read does not block us.
    //   2. Retry on IOException / UnauthorizedAccessException with linear
    //      backoff up to ~10s total. Any genuine still-alive writer would
    //      persist longer than that, so a hard throw is still meaningful.
    //   3. Use stream.Length once at open and ReadExactly to guard against
    //      a silently truncated read — a short DB inlined into HTML would
    //      be worse than a loud failure.
    private static async Task<byte[]> ReadAllBytesWithTransientRetryAsync(string path)
    {
        const int maxAttempts = 20;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    useAsync: true);

                long length = stream.Length;
                if (length > int.MaxValue)
                {
                    throw new IOException($"Temp DB at '{path}' is too large to inline ({length} bytes).");
                }

                byte[] buffer = new byte[length];
                await stream.ReadExactlyAsync(buffer.AsMemory()).ConfigureAwait(false);
                return buffer;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt)).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt)).ConfigureAwait(false);
            }
        }
    }

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string TicketSiteOwner(string siteKind)
        => $"ticket-site:{siteKind}";

    private static async ValueTask CleanupFilteredDatabaseBestEffortAsync(
        string path,
        TicketSiteCleanupHooks? cleanupHooks)
    {
        try
        {
            if (cleanupHooks?.DeleteFilteredDatabaseAsync is { } cleanup)
            {
                await cleanup(path).ConfigureAwait(false);
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                await Task.Delay(100).ConfigureAwait(false);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync(
                $"Warning: deferred cleanup of filtered snapshot database '{path}': {ex.Message}")
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask CleanupSnapshotBestEffortAsync(
        ImmutableFileSnapshot snapshot,
        TicketSiteCleanupHooks? cleanupHooks)
    {
        try
        {
            if (cleanupHooks?.DisposeSnapshotAsync is { } cleanup)
            {
                await cleanup(snapshot).ConfigureAwait(false);
            }
            else
            {
                await snapshot.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync(
                $"Warning: deferred cleanup of private snapshot '{snapshot.Path}': {ex.Message}")
                .ConfigureAwait(false);
        }
    }

    private static bool TryParseArgs(string[] args, out CliOptions options, out string? error)
    {
        string? preparerSnapshot = null;
        bool preparerSnapshotSupplied = false;
        string? plannerSnapshot = null;
        bool plannerSnapshotSupplied = false;
        string? snapshotDescriptor = null;
        string? outPath = null;
        string title = DefaultTitle;
        string? filterSpec = null;
        string? filterProject = null;
        string? filterWg = null;
        bool force = false;
        bool help = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--preparer-snapshot":
                    preparerSnapshotSupplied = true;
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        options = Default();
                        error = $"Missing value for {arg}";
                        return false;
                    }
                    preparerSnapshot = args[++i];
                    break;
                case "--planner-snapshot":
                    plannerSnapshotSupplied = true;
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        options = Default();
                        error = $"Missing value for {arg}";
                        return false;
                    }
                    plannerSnapshot = args[++i];
                    break;
                case "--snapshot-descriptor":
                    if (i + 1 >= args.Length)
                    {
                        options = Default();
                        error = $"Missing value for {arg}";
                        return false;
                    }
                    snapshotDescriptor = args[++i];
                    break;
                case "--out":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    outPath = args[++i];
                    break;
                case "--title":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    title = args[++i];
                    break;
                case "--spec":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    filterSpec = args[++i];
                    break;
                case "--project":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    filterProject = args[++i];
                    break;
                case "--wg":
                    if (i + 1 >= args.Length) { options = Default(); error = $"Missing value for {arg}"; return false; }
                    filterWg = args[++i];
                    break;
                case "--force":
                    force = true;
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                default:
                    options = Default();
                    error = $"Unknown argument: {arg}";
                    return false;
            }
        }

        options = new CliOptions(
            PreparerSnapshotPath: preparerSnapshot,
            PreparerSnapshotSupplied: preparerSnapshotSupplied,
            PlannerSnapshotPath: plannerSnapshot,
            PlannerSnapshotSupplied: plannerSnapshotSupplied,
            SnapshotDescriptorPath: snapshotDescriptor,
            OutPath: outPath,
            Title: title,
            FilterSpec: filterSpec,
            FilterProject: filterProject,
            FilterWorkGroup: filterWg,
            Force: force,
            Help: help);
        error = null;
        return true;

        static CliOptions Default() => new(
            null,
            false,
            null,
            false,
            null,
            null,
            DefaultTitle,
            null,
            null,
            null,
            false,
            false);
    }

    private static void WriteUsage(TextWriter w)
    {
        w.WriteLine();
        w.WriteLine(
            "Usage: ticket-site (--preparer-snapshot <path> | --planner-snapshot <path>) [options]");
        w.WriteLine();
        w.WriteLine("  Exactly one immutable processor snapshot input is required.");
        w.WriteLine("  --preparer-snapshot <path> Validated immutable Preparer snapshot. Builds discussion/.");
        w.WriteLine("  --planner-snapshot <path>  Validated immutable Planner snapshot. Builds applying/.");
        w.WriteLine("  --snapshot-descriptor <path> Trusted descriptor JSON required with a snapshot.");
        w.WriteLine("  --out <path>           Output root (default: ./cache/jira-ticket-site).");
        w.WriteLine($"  --title <string>       Site title (default: \"{DefaultTitle}\").");
        w.WriteLine("  --spec <name>          Filter tickets by hydrated specification.");
        w.WriteLine("  --project <key>        Filter by Jira project key.");
        w.WriteLine("  --wg <name|code>       Filter by workgroup (name, code, or clean name).");
        w.WriteLine("  --force                Overwrite a sub-site dir whose marker has a different filter set.");
        w.WriteLine("  --help                 Show this help.");
    }
}
