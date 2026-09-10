using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Common.IO;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using FhirAugury.Publishing.Tickets;

namespace FhirAugury.Tools.TicketSite;

internal sealed record TicketSiteCliTestHooks(
    Func<string, CancellationToken, Task>? BeforeLegacySnapshotCaptureAsync = null);

public static class Program
{
    private const string DefaultTitle = "Ticket Site";
    private const string DefaultOutSubpath = "cache/jira-ticket-site";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

    public static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await RunAsync(args, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    internal static async Task<int> RunAsync(
        string[] args,
        CancellationToken ct = default,
        TicketSiteCliTestHooks? testHooks = null)
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
                "Snapshot mode requires --snapshot-descriptor <path>.")
                .ConfigureAwait(false);
            return 2;
        }

        TicketSiteKind siteKind = options.PreparerSnapshotSupplied
            ? TicketSiteKind.Discussion
            : TicketSiteKind.Applying;
        string serviceName = options.PreparerSnapshotSupplied
            ? "Preparer"
            : "Planner";
        string databasePath = Path.GetFullPath(
            options.PreparerSnapshotSupplied
                ? options.PreparerSnapshotPath!
                : options.PlannerSnapshotPath!);
        string descriptorPath =
            Path.GetFullPath(options.SnapshotDescriptorPath);
        string outputRoot = Path.GetFullPath(
            options.OutPath ??
            Path.Combine(
                Directory.GetCurrentDirectory(),
                DefaultOutSubpath));
        string siteOutput = Path.Combine(
            outputRoot,
            siteKind == TicketSiteKind.Discussion
                ? "discussion"
                : "applying");

        if (!File.Exists(databasePath))
        {
            await Console.Error.WriteLineAsync(
                $"Database file not found: {databasePath}").ConfigureAwait(false);
            return 1;
        }

        string? temporaryPairDirectory = null;
        try
        {
            RejectLegacyInputSidecars(databasePath);
            try
            {
                StagedDirectoryPublisher.RejectSourcePathsWithinPublication(
                    outputRoot,
                    databasePath,
                    descriptorPath);
                StagedDirectoryPublisher.RejectSourcePathsWithinPublication(
                    siteOutput,
                    databasePath,
                    descriptorPath);
            }
            catch (InvalidOperationException ex)
            {
                throw new TicketSitePublishException(
                    TicketSitePublishFailure.InvalidRequest,
                    ex.Message,
                    ex);
            }

            (VerifiedAuthoringSnapshotPair pair, string? ownedDirectory) =
                await OpenPairAsync(
                    serviceName,
                    databasePath,
                    descriptorPath,
                    testHooks,
                    ct).ConfigureAwait(false);
            temporaryPairDirectory = ownedDirectory;

            TicketSitePublisher publisher = new(
                warning => Console.Error.WriteLine(warning));
            TicketSitePublishResult result = await publisher.PublishAsync(
                new TicketSitePublishRequest(
                    pair,
                    siteKind,
                    outputRoot,
                    options.Title,
                    new TicketSiteFilters(
                        options.FilterSpec,
                        options.FilterProject,
                        options.FilterWorkGroup),
                    options.Force),
                ct).ConfigureAwait(false);

            EchoResolvedFilter(
                "--spec",
                options.FilterSpec,
                result.ResolvedFilters.Specification);
            EchoResolvedFilter(
                "--project",
                options.FilterProject,
                result.ResolvedFilters.Project);
            EchoResolvedFilter(
                "--wg",
                options.FilterWorkGroup,
                result.ResolvedFilters.WorkGroup);
            Console.WriteLine(TicketSiteManifest.ToSummaryJson(result.Manifest));
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync(
                "Snapshot site publication canceled.").ConfigureAwait(false);
            return 1;
        }
        catch (TicketSitePublishException ex)
        {
            string? prefix = ex.Failure switch
            {
                TicketSitePublishFailure.SnapshotValidation =>
                    "Snapshot validation failed",
                TicketSitePublishFailure.FilterValidation => null,
                _ => "Snapshot site publication failed",
            };
            await Console.Error.WriteLineAsync(
                prefix is null ? ex.Message : $"{prefix}: {ex.Message}")
                .ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            CryptographicException or InvalidOperationException or
            ArgumentException or NotSupportedException or FormatException)
        {
            await Console.Error.WriteLineAsync(
                $"Snapshot validation failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            if (temporaryPairDirectory is not null)
            {
                await DeleteTemporaryPairBestEffortAsync(
                    temporaryPairDirectory).ConfigureAwait(false);
            }
        }
    }

    private static async Task<(VerifiedAuthoringSnapshotPair Pair, string? OwnedDirectory)>
        OpenPairAsync(
            string serviceName,
            string databasePath,
            string descriptorPath,
            TicketSiteCliTestHooks? testHooks,
            CancellationToken ct)
    {
        string pairDirectory =
            Path.GetDirectoryName(databasePath) ?? Environment.CurrentDirectory;
        string readyPath = Path.Combine(
            pairDirectory,
            AuthoringSnapshotPairManifest.ReadyFileName);
        if (File.Exists(readyPath))
        {
            AuthoringSnapshotPairManifest? ready =
                JsonSerializer.Deserialize<AuthoringSnapshotPairManifest>(
                    await File.ReadAllTextAsync(readyPath, ct)
                        .ConfigureAwait(false),
                    JsonOptions);
            if (ready is not null &&
                string.Equals(
                    Path.GetFullPath(
                        Path.Combine(pairDirectory, ready.DatabaseFileName)),
                    databasePath,
                    PathComparison) &&
                string.Equals(
                    Path.GetFullPath(
                        Path.Combine(pairDirectory, ready.DescriptorFileName)),
                    descriptorPath,
                    PathComparison))
            {
                VerifiedAuthoringSnapshotPair durable =
                    await new AuthoringSnapshotPairVerifier()
                        .VerifyReadyPairAsync(
                            serviceName,
                            ready.RunId,
                            pairDirectory,
                            ct).ConfigureAwait(false);
                return (durable, null);
            }
        }

        byte[] descriptorBytes =
            await File.ReadAllBytesAsync(descriptorPath, ct).ConfigureAwait(false);
        AuthoringSnapshotDescriptor descriptor =
            JsonSerializer.Deserialize<AuthoringSnapshotDescriptor>(
                descriptorBytes,
                JsonOptions)
            ?? throw new InvalidOperationException(
                $"Descriptor file '{descriptorPath}' is empty.");
        if (!string.Equals(
                descriptor.FileName,
                Path.GetFileName(databasePath),
                PathComparison))
        {
            throw new InvalidOperationException(
                "Descriptor file name or size is inconsistent with the snapshot input.");
        }

        if (testHooks?.BeforeLegacySnapshotCaptureAsync is { } beforeCapture)
        {
            await beforeCapture(databasePath, ct).ConfigureAwait(false);
        }
        await using ImmutableFileSnapshot immutableDatabase =
            await ImmutableFileSnapshot.CreateAsync(
                databasePath,
                ct,
                warning => Console.Error.WriteLine($"Warning: {warning}"))
                .ConfigureAwait(false);

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"fhir-augury-ticket-site-pair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string copiedDatabasePath =
                Path.Combine(temporaryDirectory, descriptor.FileName);
            string descriptorFileName =
                $"snapshot-descriptor-{Guid.NewGuid():N}.json";
            string copiedDescriptorPath =
                Path.Combine(temporaryDirectory, descriptorFileName);
            await CopyFileAsync(
                immutableDatabase.Path,
                copiedDatabasePath,
                ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                copiedDescriptorPath,
                descriptorBytes,
                ct).ConfigureAwait(false);

            AuthoringSnapshotPairManifest manifest = new(
                AuthoringSnapshotPairManifest.CurrentFormatVersion,
                serviceName,
                descriptor.RunId,
                descriptor.SnapshotId,
                descriptorFileName,
                descriptor.FileName,
                descriptor.SizeBytes,
                Convert.ToHexString(SHA256.HashData(descriptorBytes))
                    .ToLowerInvariant(),
                descriptor.Sha256);
            await File.WriteAllTextAsync(
                Path.Combine(
                    temporaryDirectory,
                    AuthoringSnapshotPairManifest.ReadyFileName),
                JsonSerializer.Serialize(manifest, JsonOptions),
                ct).ConfigureAwait(false);

            VerifiedAuthoringSnapshotPair pair =
                await new AuthoringSnapshotPairVerifier()
                    .VerifyReadyPairAsync(
                        serviceName,
                        descriptor.RunId,
                        temporaryDirectory,
                        ct).ConfigureAwait(false);
            return (pair, temporaryDirectory);
        }
        catch
        {
            await DeleteDirectoryAsync(temporaryDirectory).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken ct)
    {
        await using FileStream input = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream output = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void RejectLegacyInputSidecars(string databasePath)
    {
        string[] sidecars = ["-wal", "-shm", "-journal"];
        string[] existing = sidecars
            .Select(suffix => databasePath + suffix)
            .Where(File.Exists)
            .ToArray();
        if (existing.Length > 0)
        {
            throw new TicketSitePublishException(
                TicketSitePublishFailure.Publication,
                "Snapshot source has SQLite sidecar files and is not immutable: " +
                string.Join(", ", existing.Select(Path.GetFileName)));
        }
    }

    private static void EchoResolvedFilter(
        string flag,
        string? raw,
        string? canonical)
    {
        if (raw is null || canonical is null ||
            string.Equals(raw, canonical, StringComparison.Ordinal))
        {
            return;
        }
        Console.WriteLine($"Resolved {flag} '{raw}' → '{canonical}'.");
    }

    private static async Task DeleteTemporaryPairBestEffortAsync(string path)
    {
        try
        {
            await DeleteDirectoryAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync(
                $"Warning: deferred cleanup of verified snapshot pair '{path}': {ex.Message}")
                .ConfigureAwait(false);
        }
    }

    private static async Task DeleteDirectoryAsync(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                return;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException &&
                attempt < 5)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt))
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool TryParseArgs(
        string[] args,
        out CliOptions options,
        out string? error)
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
                    if (i + 1 >= args.Length ||
                        args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        options = Default();
                        error = $"Missing value for {arg}";
                        return false;
                    }
                    preparerSnapshot = args[++i];
                    break;
                case "--planner-snapshot":
                    plannerSnapshotSupplied = true;
                    if (i + 1 >= args.Length ||
                        args[i + 1].StartsWith("--", StringComparison.Ordinal))
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
                    if (i + 1 >= args.Length)
                    {
                        options = Default();
                        error = $"Missing value for {arg}";
                        return false;
                    }
                    outPath = args[++i];
                    break;
                case "--title":
                    if (i + 1 >= args.Length)
                    {
                        options = Default();
                        error = $"Missing value for {arg}";
                        return false;
                    }
                    title = args[++i];
                    break;
                case "--spec":
                    if (i + 1 >= args.Length)
                    {
                        options = Default();
                        error = $"Missing value for {arg}";
                        return false;
                    }
                    filterSpec = args[++i];
                    break;
                case "--project":
                    if (i + 1 >= args.Length)
                    {
                        options = Default();
                        error = $"Missing value for {arg}";
                        return false;
                    }
                    filterProject = args[++i];
                    break;
                case "--wg":
                    if (i + 1 >= args.Length)
                    {
                        options = Default();
                        error = $"Missing value for {arg}";
                        return false;
                    }
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
            preparerSnapshot,
            preparerSnapshotSupplied,
            plannerSnapshot,
            plannerSnapshotSupplied,
            snapshotDescriptor,
            outPath,
            title,
            filterSpec,
            filterProject,
            filterWg,
            force,
            help);
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

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine();
        writer.WriteLine(
            "Usage: ticket-site (--preparer-snapshot <path> | --planner-snapshot <path>) [options]");
        writer.WriteLine();
        writer.WriteLine("  Exactly one immutable processor snapshot input is required.");
        writer.WriteLine("  --preparer-snapshot <path> Validated immutable Preparer snapshot. Builds discussion/.");
        writer.WriteLine("  --planner-snapshot <path>  Validated immutable Planner snapshot. Builds applying/.");
        writer.WriteLine("  --snapshot-descriptor <path> Trusted descriptor JSON required with a snapshot.");
        writer.WriteLine("  --out <path>           Output root (default: ./cache/jira-ticket-site).");
        writer.WriteLine($"  --title <string>       Site title (default: \"{DefaultTitle}\").");
        writer.WriteLine("  --spec <name>          Filter tickets by hydrated specification.");
        writer.WriteLine("  --project <key>        Filter by Jira project key.");
        writer.WriteLine("  --wg <name|code>       Filter by workgroup (name, code, or clean name).");
        writer.WriteLine("  --force                Overwrite a sub-site dir whose marker has a different filter set.");
        writer.WriteLine("  --help                 Show this help.");
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
