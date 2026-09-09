using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processing.Client;

public sealed record AuthoringSnapshotPairManifest(
    int FormatVersion,
    string ServiceName,
    string RunId,
    string SnapshotId,
    string DescriptorFileName,
    string DatabaseFileName,
    long SizeBytes,
    string DescriptorSha256,
    string DatabaseSha256)
{
    public const int CurrentFormatVersion = 1;
    public const string ReadyFileName = "verified-pair.json";
}

public sealed class VerifiedAuthoringSnapshotPair
{
    internal VerifiedAuthoringSnapshotPair(
        AuthoringSnapshotPairManifest manifest,
        AuthoringSnapshotDescriptor descriptor,
        string directoryPath,
        string descriptorPath,
        string databasePath,
        string? readyManifestPath)
    {
        Manifest = manifest;
        Descriptor = descriptor;
        DirectoryPath = directoryPath;
        DescriptorPath = descriptorPath;
        DatabasePath = databasePath;
        ReadyManifestPath = readyManifestPath;
    }

    public AuthoringSnapshotPairManifest Manifest { get; }

    public AuthoringSnapshotDescriptor Descriptor { get; }

    public string ServiceName => Manifest.ServiceName;

    public string RunId => Manifest.RunId;

    public string SnapshotId => Manifest.SnapshotId;

    public string DirectoryPath { get; }

    public string DescriptorPath { get; }

    public string DatabasePath { get; }

    public string? ReadyManifestPath { get; }

    public bool IsDurable => ReadyManifestPath is not null;
}

public sealed class AuthoringSnapshotPairVerifier
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };

    private static readonly HashSet<string> ReservedWindowsNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "CLOCK$",
            "CONIN$",
            "CONOUT$",
            "COM1",
            "COM2",
            "COM3",
            "COM¹",
            "COM²",
            "COM³",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT¹",
            "LPT²",
            "LPT³",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
        };

    public async Task<VerifiedAuthoringSnapshotPair>
        VerifyReadyPairAsync(
            string serviceName,
            string runId,
            string pairDirectory,
            CancellationToken ct = default)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairDirectory);

        string directory = Path.GetFullPath(pairDirectory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Snapshot pair directory '{directory}' was not found.");
        }
        RejectReparsePoint(directory, "Snapshot pair directory");

        string manifestPath = Path.Combine(
            directory,
            AuthoringSnapshotPairManifest.ReadyFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"Snapshot pair directory '{directory}' has no {AuthoringSnapshotPairManifest.ReadyFileName} ready manifest.");
        }
        RejectReparsePoint(manifestPath, "Snapshot pair ready manifest");

        AuthoringSnapshotPairManifest manifest =
            await ReadJsonAsync<AuthoringSnapshotPairManifest>(
                manifestPath,
                "Snapshot pair ready manifest",
                ct);
        ValidateManifest(manifest, service, runId);
        ValidateDurablePairFileNames(
            manifest.DatabaseFileName,
            manifest.DescriptorFileName);

        string descriptorPath = ResolveImmediateChild(
            directory,
            manifest.DescriptorFileName);
        string databasePath = ResolveImmediateChild(
            directory,
            manifest.DatabaseFileName);
        RequireRegularFile(descriptorPath, "Snapshot descriptor");
        RequireRegularFile(databasePath, "Snapshot database");

        byte[] descriptorBytes =
            await File.ReadAllBytesAsync(descriptorPath, ct);
        string descriptorSha256 = HashBytes(descriptorBytes);
        if (!IsSha256(manifest.DescriptorSha256) ||
            !string.Equals(
                descriptorSha256,
                manifest.DescriptorSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Snapshot descriptor digest does not match the ready manifest.");
        }

        AuthoringSnapshotDescriptor descriptor =
            ParseAndValidateDescriptor(
                descriptorBytes,
                service,
                runId);
        ValidateDescriptorAgainstManifest(descriptor, manifest);
        (long sizeBytes, string databaseSha256) =
            await HashFileAsync(databasePath, ct);
        if (sizeBytes != manifest.SizeBytes ||
            sizeBytes != descriptor.SizeBytes)
        {
            throw new InvalidOperationException(
                "Snapshot database length does not match its descriptor and ready manifest.");
        }
        if (!IsSha256(manifest.DatabaseSha256) ||
            !string.Equals(
                databaseSha256,
                manifest.DatabaseSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                databaseSha256,
                descriptor.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Snapshot database digest does not match its descriptor and ready manifest.");
        }

        return new VerifiedAuthoringSnapshotPair(
            manifest,
            descriptor,
            directory,
            descriptorPath,
            databasePath,
            manifestPath);
    }

    public async Task<VerifiedAuthoringSnapshotPair>
        VerifyLegacyPairAsync(
            string serviceName,
            string runId,
            string descriptorPath,
            string databasePath,
            CancellationToken ct = default)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        if (!string.Equals(
                service,
                AuthoringServiceBinding.BallotNotes,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"A legacy loose snapshot pair with processor kind 'jira-fhir' cannot prove whether it belongs to {AuthoringServiceBinding.Preparer} or {AuthoringServiceBinding.Planner}. A trusted durable pair binding is required.");
        }

        return await VerifyLegacyPairCoreAsync(
            service,
            runId,
            descriptorPath,
            databasePath,
            trustedManifest: null,
            ct);
    }

    internal Task<VerifiedAuthoringSnapshotPair>
        VerifyLegacyPairAsync(
            VerifiedAuthoringSnapshotPair trustedPair,
            string descriptorPath,
            string databasePath,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trustedPair);
        return VerifyLegacyPairCoreAsync(
            trustedPair.ServiceName,
            trustedPair.RunId,
            descriptorPath,
            databasePath,
            trustedPair.Manifest,
            ct);
    }

    internal async Task<VerifiedAuthoringSnapshotPair>
        VerifyManagedReadyPairAsync(
            string pairDirectory,
            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairDirectory);

        string directory = Path.GetFullPath(pairDirectory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Snapshot pair directory '{directory}' was not found.");
        }
        RejectReparsePoint(directory, "Snapshot pair directory");

        string manifestPath = Path.Combine(
            directory,
            AuthoringSnapshotPairManifest.ReadyFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"Snapshot pair directory '{directory}' has no {AuthoringSnapshotPairManifest.ReadyFileName} ready manifest.");
        }
        RejectReparsePoint(manifestPath, "Snapshot pair ready manifest");

        AuthoringSnapshotPairManifest manifest =
            await ReadJsonAsync<AuthoringSnapshotPairManifest>(
                manifestPath,
                "Snapshot pair ready manifest",
                ct);
        string service =
            AuthoringServiceBinding.Normalize(manifest.ServiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifest.RunId);
        VerifiedAuthoringSnapshotPair pair =
            await VerifyReadyPairAsync(
                service,
                manifest.RunId,
                directory,
                ct);

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        HashSet<string> ownedPaths = new(pathComparer)
        {
            pair.DatabasePath,
            pair.DescriptorPath,
            pair.ReadyManifestPath!,
        };
        string[] entries = Directory
            .EnumerateFileSystemEntries(directory)
            .Select(Path.GetFullPath)
            .ToArray();
        if (entries.Length != ownedPaths.Count ||
            entries.Any(entry => !ownedPaths.Contains(entry)))
        {
            throw new InvalidOperationException(
                $"Snapshot pair directory '{directory}' contains paths that are not owned by its ready manifest.");
        }

        return pair;
    }

    private static async Task<VerifiedAuthoringSnapshotPair>
        VerifyLegacyPairCoreAsync(
            string service,
            string runId,
            string descriptorPath,
            string databasePath,
            AuthoringSnapshotPairManifest? trustedManifest,
            CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string fullDescriptorPath = Path.GetFullPath(descriptorPath);
        string fullDatabasePath = Path.GetFullPath(databasePath);
        if (PathsEqual(fullDescriptorPath, fullDatabasePath))
        {
            throw new ArgumentException(
                "Snapshot descriptor and database paths must be different.");
        }
        ValidateSafeWindowsFileName(
            Path.GetFileName(fullDescriptorPath),
            "descriptor file name");
        ValidateSafeWindowsFileName(
            Path.GetFileName(fullDatabasePath),
            "database file name");
        RequireRegularFile(fullDescriptorPath, "Snapshot descriptor");
        RequireRegularFile(fullDatabasePath, "Snapshot database");

        byte[] descriptorBytes =
            await File.ReadAllBytesAsync(fullDescriptorPath, ct);
        AuthoringSnapshotDescriptor descriptor =
            ParseAndValidateDescriptor(
                descriptorBytes,
                service,
                runId);
        if (!string.Equals(
                descriptor.FileName,
                Path.GetFileName(fullDatabasePath),
                PathComparison))
        {
            throw new InvalidOperationException(
                $"Snapshot database file name '{Path.GetFileName(fullDatabasePath)}' does not match descriptor file name '{descriptor.FileName}'.");
        }

        (long sizeBytes, string databaseSha256) =
            await HashFileAsync(fullDatabasePath, ct);
        if (sizeBytes != descriptor.SizeBytes)
        {
            throw new InvalidOperationException(
                "Snapshot database length does not match its descriptor.");
        }
        if (!string.Equals(
                databaseSha256,
                descriptor.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Snapshot database digest does not match its descriptor.");
        }
        string descriptorSha256 = HashBytes(descriptorBytes);
        if (trustedManifest is not null &&
            (!string.Equals(
                 trustedManifest.ServiceName,
                 service,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 trustedManifest.RunId,
                 runId,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 trustedManifest.SnapshotId,
                 descriptor.SnapshotId,
                 StringComparison.Ordinal) ||
             trustedManifest.SizeBytes != sizeBytes ||
             !string.Equals(
                 trustedManifest.DescriptorSha256,
                 descriptorSha256,
                 StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(
                 trustedManifest.DatabaseSha256,
                 databaseSha256,
                 StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Legacy snapshot pair does not match its trusted durable pair binding.");
        }

        AuthoringSnapshotPairManifest manifest = new(
            AuthoringSnapshotPairManifest.CurrentFormatVersion,
            service,
            runId,
            descriptor.SnapshotId,
            Path.GetFileName(fullDescriptorPath),
            Path.GetFileName(fullDatabasePath),
            sizeBytes,
            descriptorSha256,
            databaseSha256);
        return new VerifiedAuthoringSnapshotPair(
            manifest,
            descriptor,
            Path.GetDirectoryName(fullDatabasePath)
                ?? Environment.CurrentDirectory,
            fullDescriptorPath,
            fullDatabasePath,
            readyManifestPath: null);
    }

    internal static AuthoringSnapshotDescriptor ParseAndValidateDescriptor(
        byte[] descriptorBytes,
        string serviceName,
        string runId)
    {
        AuthoringSnapshotDescriptor descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<AuthoringSnapshotDescriptor>(
                    descriptorBytes,
                    JsonOptions)
                ?? throw new InvalidOperationException(
                    "Snapshot descriptor response was empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Snapshot descriptor is not valid JSON.",
                ex);
        }

        string service = AuthoringServiceBinding.Normalize(serviceName);
        if (!string.Equals(
                descriptor.RunId,
                runId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Snapshot descriptor belongs to run '{descriptor.RunId}', not '{runId}'.");
        }
        if (string.IsNullOrWhiteSpace(descriptor.SnapshotId))
        {
            throw new InvalidOperationException(
                "Snapshot descriptor has no snapshot identifier.");
        }
        string expectedProcessorKind =
            AuthoringServiceBinding.GetProcessorKind(service);
        if (!string.Equals(
                descriptor.ProcessorKind,
                expectedProcessorKind,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Snapshot descriptor processor '{descriptor.ProcessorKind}' does not match authoring service '{service}'.");
        }
        ValidateSafeWindowsFileName(
            descriptor.FileName,
            "snapshot database file name");
        if (descriptor.SizeBytes < 0)
        {
            throw new InvalidOperationException(
                "Snapshot descriptor size cannot be negative.");
        }
        if (!IsSha256(descriptor.Sha256))
        {
            throw new InvalidOperationException(
                "Snapshot descriptor does not contain a valid SHA-256 digest.");
        }
        return descriptor;
    }

    internal static void ValidateDurablePairFileNames(
        string databaseFileName,
        string descriptorFileName)
    {
        ValidateSafeWindowsFileName(
            databaseFileName,
            "snapshot database file name");
        ValidateSafeWindowsFileName(
            descriptorFileName,
            "snapshot descriptor file name");
        if (string.Equals(
                databaseFileName,
                descriptorFileName,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                databaseFileName,
                AuthoringSnapshotPairManifest.ReadyFileName,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                descriptorFileName,
                AuthoringSnapshotPairManifest.ReadyFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Snapshot pair file names must be distinct.");
        }
    }

    internal static void ValidateSafeWindowsFileName(
        string? fileName,
        string description)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName is "." or ".." ||
            Path.IsPathRooted(fileName) ||
            fileName.Contains('/') ||
            fileName.Contains('\\') ||
            fileName.EndsWith(' ') ||
            fileName.EndsWith('.') ||
            fileName.Any(character =>
                character < ' ' ||
                character is '<' or '>' or ':' or '"' or '|' or '?' or '*'))
        {
            throw new InvalidOperationException(
                $"Snapshot {description} '{fileName}' is not a safe Windows file name.");
        }

        string deviceName = fileName.Split('.')[0].TrimEnd(' ', '.');
        if (ReservedWindowsNames.Contains(deviceName))
        {
            throw new InvalidOperationException(
                $"Snapshot {description} '{fileName}' is a reserved Windows device name.");
        }
    }

    internal static async Task<(long SizeBytes, string Sha256)> HashFileAsync(
        string path,
        CancellationToken ct)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = stream.Length;
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        if (stream.Length != length)
        {
            throw new IOException(
                $"Snapshot file '{path}' changed while it was being verified.");
        }
        return (
            length,
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    internal static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            PathComparison);

    private static void ValidateManifest(
        AuthoringSnapshotPairManifest manifest,
        string serviceName,
        string runId)
    {
        if (manifest.FormatVersion !=
            AuthoringSnapshotPairManifest.CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported snapshot pair manifest version {manifest.FormatVersion}.");
        }
        if (!string.Equals(
                manifest.ServiceName,
                serviceName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Snapshot pair belongs to service '{manifest.ServiceName}', not '{serviceName}'.");
        }
        if (!string.Equals(
                manifest.RunId,
                runId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Snapshot pair belongs to run '{manifest.RunId}', not '{runId}'.");
        }
        if (string.IsNullOrWhiteSpace(manifest.SnapshotId))
        {
            throw new InvalidOperationException(
                "Snapshot pair manifest has no snapshot identifier.");
        }
        if (manifest.SizeBytes < 0)
        {
            throw new InvalidOperationException(
                "Snapshot pair manifest size cannot be negative.");
        }
    }

    private static void ValidateDescriptorAgainstManifest(
        AuthoringSnapshotDescriptor descriptor,
        AuthoringSnapshotPairManifest manifest)
    {
        if (!string.Equals(
                descriptor.SnapshotId,
                manifest.SnapshotId,
                StringComparison.Ordinal) ||
            !string.Equals(
                descriptor.FileName,
                manifest.DatabaseFileName,
                StringComparison.Ordinal) ||
            descriptor.SizeBytes != manifest.SizeBytes)
        {
            throw new InvalidOperationException(
                "Snapshot descriptor coordinates do not match the ready manifest.");
        }
    }

    private static async Task<T> ReadJsonAsync<T>(
        string path,
        string description,
        CancellationToken ct)
    {
        try
        {
            await using FileStream input = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<T>(
                    input,
                    JsonOptions,
                    ct)
                ?? throw new InvalidOperationException(
                    $"{description} '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{description} '{path}' is not valid JSON.",
                ex);
        }
    }

    private static string ResolveImmediateChild(
        string directory,
        string fileName)
    {
        string path = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!string.Equals(
                Path.GetDirectoryName(path),
                directory,
                PathComparison))
        {
            throw new InvalidOperationException(
                $"Snapshot pair file '{fileName}' escapes its pair directory.");
        }
        return path;
    }

    private static void RequireRegularFile(
        string path,
        string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"{description} file was not found.",
                path);
        }
        RejectReparsePoint(path, description);
    }

    private static void RejectReparsePoint(
        string path,
        string description)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                $"{description} '{path}' cannot be a reparse point.");
        }
    }

    private static string HashBytes(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256(string? value)
        => value is { Length: 64 } &&
            value.All(character =>
                character is >= '0' and <= '9' or
                    >= 'a' and <= 'f' or
                    >= 'A' and <= 'F');

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
