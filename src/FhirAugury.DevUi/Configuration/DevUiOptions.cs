using Microsoft.Extensions.Options;

namespace FhirAugury.DevUi.Configuration;

public sealed class DevUiOptions
{
    public const string SectionName = "DevUi";

    public string OrchestratorAddress { get; set; } =
        "http://localhost:5150";

    public int RecentRunLimit { get; set; } = 20;

    public TimeSpan RunPollInterval { get; set; } =
        TimeSpan.FromSeconds(3);

    public string CacheRoot { get; set; } =
        Path.Combine("..", "..", "cache");

    public string SnapshotCacheRoot { get; set; } =
        "devui-authoring-snapshots";

    public string ReviewSitesRoot { get; set; } =
        "devui-review-sites";
}

public interface IFileSystemInspector
{
    bool Exists(string path);

    FileAttributes GetAttributes(string path);

    void CreateDirectory(string path);
}

public sealed class PhysicalFileSystemInspector : IFileSystemInspector
{
    public bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    public FileAttributes GetAttributes(string path) =>
        File.GetAttributes(path);

    public void CreateDirectory(string path) =>
        Directory.CreateDirectory(path);
}

public sealed class DevUiOptionsValidator : IValidateOptions<DevUiOptions>
{
    private readonly string _contentRoot;
    private readonly string? _repositoryRoot;
    private readonly IFileSystemInspector _fileSystem;

    public DevUiOptionsValidator(
        IHostEnvironment environment,
        IFileSystemInspector fileSystem)
        : this(
            environment.ContentRootPath,
            DevUiPathGuard.FindRepositoryRoot(
                environment.ContentRootPath),
            fileSystem)
    {
    }

    public DevUiOptionsValidator(
        string contentRoot,
        string? repositoryRoot,
        IFileSystemInspector fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        _contentRoot = Path.GetFullPath(contentRoot);
        _repositoryRoot = string.IsNullOrWhiteSpace(repositoryRoot)
            ? null
            : Path.GetFullPath(repositoryRoot);
        _fileSystem = fileSystem ??
            throw new ArgumentNullException(nameof(fileSystem));
    }

    public ValidateOptionsResult Validate(
        string? name,
        DevUiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];

        if (!Uri.TryCreate(
                options.OrchestratorAddress,
                UriKind.Absolute,
                out Uri? orchestratorAddress) ||
            orchestratorAddress.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(orchestratorAddress.Host) ||
            !string.IsNullOrEmpty(orchestratorAddress.UserInfo))
        {
            failures.Add(
                "DevUi:OrchestratorAddress must be an absolute HTTP or HTTPS address without user information.");
        }

        if (options.RecentRunLimit is < 1 or > 100)
        {
            failures.Add(
                "DevUi:RecentRunLimit must be between 1 and 100.");
        }

        if (options.RunPollInterval <= TimeSpan.Zero)
        {
            failures.Add(
                "DevUi:RunPollInterval must be greater than zero.");
        }

        string? cacheRoot = TryResolve(
            options.CacheRoot,
            _contentRoot,
            "DevUi:CacheRoot",
            failures);
        string? snapshotRoot = cacheRoot is null
            ? null
            : TryResolve(
                options.SnapshotCacheRoot,
                cacheRoot,
                "DevUi:SnapshotCacheRoot",
                failures);
        string? reviewRoot = cacheRoot is null
            ? null
            : TryResolve(
                options.ReviewSitesRoot,
                cacheRoot,
                "DevUi:ReviewSitesRoot",
                failures);

        if (cacheRoot is not null &&
            snapshotRoot is not null &&
            reviewRoot is not null)
        {
            failures.AddRange(DevUiPathGuard.ValidateConfiguredRoots(
                cacheRoot,
                snapshotRoot,
                reviewRoot,
                _contentRoot,
                _repositoryRoot,
                _fileSystem));
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        options.OrchestratorAddress =
            orchestratorAddress!.AbsoluteUri.TrimEnd('/');
        options.CacheRoot = cacheRoot!;
        options.SnapshotCacheRoot = snapshotRoot!;
        options.ReviewSitesRoot = reviewRoot!;
        return ValidateOptionsResult.Success;
    }

    private static string? TryResolve(
        string? configuredPath,
        string relativeRoot,
        string optionName,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            failures.Add($"{optionName} must not be empty.");
            return null;
        }

        try
        {
            DevUiPathGuard.ValidatePathSyntax(
                configuredPath,
                optionName);
            string combined = Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(relativeRoot, configuredPath);
            return DevUiPathGuard.NormalizePath(combined);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            failures.Add($"{optionName} is invalid: {ex.Message}");
            return null;
        }
    }
}

public static class DevUiPathGuard
{
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

    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        ThrowIfWindowsDeviceNamespace(fullPath, "Path");
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    public static void ValidateSafeSegment(
        string segment,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment);
        if (segment is "." or ".." ||
            Path.IsPathRooted(segment) ||
            segment.Contains('/') ||
            segment.Contains('\\') ||
            segment.Contains(':') ||
            segment.EndsWith(' ') ||
            segment.EndsWith('.') ||
            segment.Any(character =>
                character < ' ' ||
                character is '<' or '>' or '"' or '|' or '?' or '*'))
        {
            throw new ArgumentException(
                $"{description} '{segment}' is not a safe Windows path segment.",
                nameof(segment));
        }

        string deviceName = segment.Split('.')[0].TrimEnd(' ', '.');
        if (ReservedWindowsNames.Contains(deviceName))
        {
            throw new ArgumentException(
                $"{description} '{segment}' is a reserved Windows device name.",
                nameof(segment));
        }
    }

    public static void ValidatePathSyntax(
        string path,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfWindowsDeviceNamespace(path, description);
        ThrowIfWindowsDeviceNamespace(
            Path.GetFullPath(path),
            description);
        string? root = Path.GetPathRoot(path);
        string remainder = string.IsNullOrEmpty(root)
            ? path
            : path[root.Length..];
        foreach (string segment in remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                continue;
            }
            ValidateSafeSegment(segment, description);
        }
    }

    private static void ThrowIfWindowsDeviceNamespace(
        string path,
        string description)
    {
        string normalizedSeparators =
            path.Replace('/', '\\');
        if (normalizedSeparators.StartsWith(
                @"\\?\",
                StringComparison.Ordinal) ||
            normalizedSeparators.StartsWith(
                @"\\.\",
                StringComparison.Ordinal) ||
            normalizedSeparators.StartsWith(
                @"\??\",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{description} cannot use a Windows device namespace.",
                nameof(path));
        }
    }

    public static IReadOnlyList<string> ValidateConfiguredRoots(
        string cacheRoot,
        string snapshotRoot,
        string reviewRoot,
        string contentRoot,
        string? repositoryRoot,
        IFileSystemInspector fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        cacheRoot = NormalizePath(cacheRoot);
        snapshotRoot = NormalizePath(snapshotRoot);
        reviewRoot = NormalizePath(reviewRoot);
        contentRoot = NormalizePath(contentRoot);
        repositoryRoot = string.IsNullOrWhiteSpace(repositoryRoot)
            ? null
            : NormalizePath(repositoryRoot);
        string webRoot = NormalizePath(
            Path.Combine(contentRoot, "wwwroot"));

        List<string> failures = [];
        string? driveRoot = Path.GetPathRoot(cacheRoot);
        if (!string.IsNullOrWhiteSpace(driveRoot) &&
            PathsEqual(cacheRoot, driveRoot))
        {
            failures.Add(
                "DevUi:CacheRoot cannot be a filesystem root.");
        }
        if (repositoryRoot is not null &&
            PathsEqual(cacheRoot, repositoryRoot))
        {
            failures.Add(
                "DevUi:CacheRoot cannot be the repository root.");
        }
        if (PathsEqual(cacheRoot, contentRoot))
        {
            failures.Add(
                "DevUi:CacheRoot cannot be the application content root.");
        }
        if (PathsEqual(cacheRoot, webRoot) ||
            IsContainedBy(cacheRoot, webRoot))
        {
            failures.Add(
                "DevUi:CacheRoot cannot be wwwroot or one of its descendants.");
        }
        if (IsContainedBy(contentRoot, cacheRoot) &&
            !PathsEqual(contentRoot, cacheRoot))
        {
            failures.Add(
                "DevUi:CacheRoot cannot contain the application content root.");
        }

        if (!IsStrictDescendant(snapshotRoot, cacheRoot))
        {
            failures.Add(
                "DevUi:SnapshotCacheRoot must be a child of DevUi:CacheRoot.");
        }
        if (!IsStrictDescendant(reviewRoot, cacheRoot))
        {
            failures.Add(
                "DevUi:ReviewSitesRoot must be a child of DevUi:CacheRoot.");
        }
        if (ContainsOrEquals(snapshotRoot, reviewRoot) ||
            ContainsOrEquals(reviewRoot, snapshotRoot))
        {
            failures.Add(
                "DevUi:SnapshotCacheRoot and DevUi:ReviewSitesRoot cannot contain one another.");
        }
        if (IsContainedBy(snapshotRoot, webRoot) ||
            PathsEqual(snapshotRoot, webRoot))
        {
            failures.Add(
                "DevUi:SnapshotCacheRoot cannot be served from wwwroot.");
        }

        AddReparseFailure(cacheRoot, "DevUi:CacheRoot", fileSystem, failures);
        AddReparseFailure(
            snapshotRoot,
            "DevUi:SnapshotCacheRoot",
            fileSystem,
            failures);
        AddReparseFailure(
            reviewRoot,
            "DevUi:ReviewSitesRoot",
            fileSystem,
            failures);
        return failures;
    }

    public static void EnsureSafeDescendant(
        string approvedRoot,
        string candidate,
        IFileSystemInspector fileSystem,
        string description)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        string root = NormalizePath(approvedRoot);
        string path = NormalizePath(candidate);
        if (!IsStrictDescendant(path, root))
        {
            throw new InvalidOperationException(
                $"{description} '{path}' escapes approved root '{root}'.");
        }

        string? reparsePoint = FindExistingReparsePoint(path, fileSystem);
        if (reparsePoint is not null)
        {
            throw new InvalidOperationException(
                $"{description} cannot traverse reparse point '{reparsePoint}'.");
        }
    }

    public static void EnsureNoReparsePoints(
        string path,
        IFileSystemInspector fileSystem,
        string description)
    {
        string fullPath = NormalizePath(path);
        string? reparsePoint =
            FindExistingReparsePoint(fullPath, fileSystem);
        if (reparsePoint is not null)
        {
            throw new InvalidOperationException(
                $"{description} cannot traverse reparse point '{reparsePoint}'.");
        }
    }

    public static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            PathComparison);

    public static string? FindRepositoryRoot(string contentRoot)
    {
        DirectoryInfo? current =
            new(NormalizePath(contentRoot));
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(
                    current.FullName,
                    "fhir-augury.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return null;
    }

    private static void AddReparseFailure(
        string path,
        string optionName,
        IFileSystemInspector fileSystem,
        ICollection<string> failures)
    {
        try
        {
            string? reparsePoint =
                FindExistingReparsePoint(path, fileSystem);
            if (reparsePoint is not null)
            {
                failures.Add(
                    $"{optionName} cannot traverse reparse point '{reparsePoint}'.");
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
                NotSupportedException)
        {
            failures.Add(
                $"{optionName} could not be inspected safely: {ex.Message}");
        }
    }

    private static string? FindExistingReparsePoint(
        string path,
        IFileSystemInspector fileSystem)
    {
        string fullPath = NormalizePath(path);
        string root = Path.GetPathRoot(fullPath) ??
            throw new InvalidOperationException(
                $"Path '{fullPath}' has no filesystem root.");
        string current = root;
        if (fileSystem.Exists(current) &&
            fileSystem.GetAttributes(current)
                .HasFlag(FileAttributes.ReparsePoint))
        {
            return NormalizePath(current);
        }

        string remainder = fullPath[root.Length..];
        foreach (string segment in remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!fileSystem.Exists(current))
            {
                continue;
            }
            if (fileSystem.GetAttributes(current)
                .HasFlag(FileAttributes.ReparsePoint))
            {
                return NormalizePath(current);
            }
        }
        return null;
    }

    private static bool IsStrictDescendant(
        string candidate,
        string root) =>
        !PathsEqual(candidate, root) &&
        IsContainedBy(candidate, root);

    private static bool IsContainedBy(
        string candidate,
        string root)
    {
        string normalizedCandidate = NormalizePath(candidate);
        string normalizedRoot = NormalizePath(root);
        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            PathComparison);
    }

    private static bool ContainsOrEquals(
        string candidateParent,
        string candidateChild) =>
        PathsEqual(candidateParent, candidateChild) ||
        IsContainedBy(candidateChild, candidateParent);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
