using FhirAugury.DevUi.Configuration;
using FhirAugury.DevUi.Models;
using FhirAugury.Processing.Client;
using FhirAugury.Publishing.Tickets;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace FhirAugury.DevUi.Services;

public interface IReviewSiteStore
{
    void EnsureRoots();

    ReviewSiteCoordinates GetCoordinates(
        string workflow,
        string runId);

    void RevalidateForPublication(
        ReviewSiteCoordinates coordinates);

    Task<VerifiedAuthoringSnapshotPair?> TryOpenVerifiedPairAsync(
        ReviewSiteCoordinates coordinates,
        CancellationToken ct = default);

    Task<ReviewSitePublication?> TryReconstructAsync(
        string workflow,
        string runId,
        CancellationToken ct = default);

    Task<ReviewSitePublication> ValidatePublishedSiteAsync(
        ReviewSiteCoordinates coordinates,
        VerifiedAuthoringSnapshotPair pair,
        CancellationToken ct = default);
}

public sealed class ReviewSiteStore : IReviewSiteStore
{
    private readonly DevUiOptions _options;
    private readonly TicketWorkflowCatalog _catalog;
    private readonly IFileSystemInspector _fileSystem;
    private readonly AuthoringSnapshotPairVerifier _pairVerifier = new();

    public ReviewSiteStore(
        IOptions<DevUiOptions> options,
        TicketWorkflowCatalog catalog,
        IFileSystemInspector fileSystem)
    {
        _options = options.Value;
        _catalog = catalog ??
            throw new ArgumentNullException(nameof(catalog));
        _fileSystem = fileSystem ??
            throw new ArgumentNullException(nameof(fileSystem));
    }

    internal string ReviewSitesRoot => _options.ReviewSitesRoot;

    public void EnsureRoots()
    {
        RevalidateConfiguredRoots();
        _fileSystem.CreateDirectory(_options.CacheRoot);
        DevUiPathGuard.EnsureNoReparsePoints(
            _options.CacheRoot,
            _fileSystem,
            "Dev UI cache root");

        _fileSystem.CreateDirectory(_options.SnapshotCacheRoot);
        _fileSystem.CreateDirectory(_options.ReviewSitesRoot);
        RevalidateConfiguredRoots();
    }

    public ReviewSiteCoordinates GetCoordinates(
        string workflow,
        string runId)
    {
        TicketWorkflowDefinition definition =
            _catalog.Get(workflow);
        DevUiPathGuard.ValidateSafeSegment(
            definition.RouteKey,
            "Workflow route key");
        DevUiPathGuard.ValidateSafeSegment(
            runId,
            "Authoring run identifier");
        DevUiPathGuard.ValidateSafeSegment(
            definition.SiteFolder,
            "Ticket site folder");

        string pairDirectory = DevUiPathGuard.NormalizePath(
            Path.Combine(
                _options.SnapshotCacheRoot,
                definition.RouteKey,
                runId));
        string siteRoot = DevUiPathGuard.NormalizePath(
            Path.Combine(
                _options.ReviewSitesRoot,
                definition.RouteKey,
                runId));
        string siteDirectory = DevUiPathGuard.NormalizePath(
            Path.Combine(siteRoot, definition.SiteFolder));
        ReviewSiteCoordinates coordinates = new(
            definition,
            runId,
            pairDirectory,
            siteRoot,
            siteDirectory,
            Path.Combine(
                siteDirectory,
                TicketSiteManifest.FileName),
            $"/review-sites/{Uri.EscapeDataString(definition.RouteKey)}/{Uri.EscapeDataString(runId)}/{Uri.EscapeDataString(definition.SiteFolder)}/");
        RevalidateCoordinates(coordinates);
        return coordinates;
    }

    public void RevalidateForPublication(
        ReviewSiteCoordinates coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        RevalidateCoordinates(coordinates);
    }

    public bool IsStaticPathAllowed(string subpath)
    {
        try
        {
            string[] segments =
                ParseStaticPathSegments(subpath);
            if (segments.Length < 3 ||
                !_catalog.TryGet(
                    segments[0],
                    out TicketWorkflowDefinition workflow) ||
                !string.Equals(
                    segments[0],
                    workflow.RouteKey,
                    StringComparison.Ordinal))
            {
                return false;
            }

            bool isRunRootChooserArtifact =
                segments.Length == 3 &&
                string.Equals(
                    segments[2],
                    "index.html",
                    StringComparison.Ordinal) ||
                segments.Length == 4 &&
                string.Equals(
                    segments[2],
                    "assets",
                    StringComparison.Ordinal) &&
                string.Equals(
                    segments[3],
                    "chooser.css",
                    StringComparison.Ordinal);
            bool isWorkflowSitePath =
                string.Equals(
                    segments[2],
                    workflow.SiteFolder,
                    StringComparison.Ordinal);
            if (!isRunRootChooserArtifact &&
                !isWorkflowSitePath)
            {
                return false;
            }

            ReviewSiteCoordinates coordinates =
                GetCoordinates(
                    workflow.RouteKey,
                    segments[1]);
            if (isRunRootChooserArtifact)
            {
                string chooserPath = coordinates.SiteRoot;
                foreach (string segment in segments.Skip(2))
                {
                    chooserPath =
                        Path.Combine(chooserPath, segment);
                }
                DevUiPathGuard.EnsureSafeDescendant(
                    coordinates.SiteRoot,
                    chooserPath,
                    _fileSystem,
                    "Review-site chooser request");
                return true;
            }

            string requestedPath = coordinates.SiteDirectory;
            foreach (string segment in segments.Skip(3))
            {
                requestedPath =
                    Path.Combine(requestedPath, segment);
            }

            if (segments.Length == 3)
            {
                DevUiPathGuard.EnsureNoReparsePoints(
                    requestedPath,
                    _fileSystem,
                    "Review-site request");
            }
            else
            {
                DevUiPathGuard.EnsureSafeDescendant(
                    coordinates.SiteDirectory,
                    requestedPath,
                    _fileSystem,
                    "Review-site request");
            }
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or FormatException or
                InvalidOperationException or IOException or
                UnauthorizedAccessException or
                NotSupportedException or KeyNotFoundException)
        {
            return false;
        }
    }

    public async Task<VerifiedAuthoringSnapshotPair?>
        TryOpenVerifiedPairAsync(
            ReviewSiteCoordinates coordinates,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        RevalidateCoordinates(coordinates);
        if (!Directory.Exists(coordinates.SnapshotPairDirectory))
        {
            return null;
        }

        try
        {
            VerifiedAuthoringSnapshotPair pair =
                await _pairVerifier.VerifyReadyPairAsync(
                    coordinates.Workflow.ProcessingServiceName,
                    coordinates.RunId,
                    coordinates.SnapshotPairDirectory,
                    ct);
            RevalidateCoordinates(coordinates);
            return pair;
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or
                UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<ReviewSitePublication?> TryReconstructAsync(
        string workflow,
        string runId,
        CancellationToken ct = default)
    {
        ReviewSiteCoordinates coordinates =
            GetCoordinates(workflow, runId);
        if (!File.Exists(coordinates.SiteManifestPath))
        {
            return null;
        }

        VerifiedAuthoringSnapshotPair pair =
            await TryOpenVerifiedPairAsync(coordinates, ct)
            ?? throw new InvalidOperationException(
                $"Published site '{coordinates.SiteDirectory}' has no workflow-bound verified snapshot pair.");
        ReviewSitePublication publication =
            await ValidatePublishedSiteAsync(
                coordinates,
                pair,
                ct);
        return publication with { Reconstructed = true };
    }

    public async Task<ReviewSitePublication> ValidatePublishedSiteAsync(
        ReviewSiteCoordinates coordinates,
        VerifiedAuthoringSnapshotPair pair,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        ArgumentNullException.ThrowIfNull(pair);
        RevalidateCoordinates(coordinates);

        if (!string.Equals(
                pair.ServiceName,
                coordinates.Workflow.ProcessingServiceName,
                StringComparison.Ordinal) ||
            !string.Equals(
                pair.RunId,
                coordinates.RunId,
                StringComparison.Ordinal) ||
            !DevUiPathGuard.PathsEqual(
                pair.DirectoryPath,
                coordinates.SnapshotPairDirectory))
        {
            throw new InvalidOperationException(
                "Verified snapshot pair does not match the requested workflow and run coordinates.");
        }

        VerifiedAuthoringSnapshotPair currentPair =
            await _pairVerifier.VerifyReadyPairAsync(
                coordinates.Workflow.ProcessingServiceName,
                coordinates.RunId,
                coordinates.SnapshotPairDirectory,
                ct);
        if (!File.Exists(coordinates.SiteManifestPath))
        {
            throw new FileNotFoundException(
                "The expected ticket site manifest was not found.",
                coordinates.SiteManifestPath);
        }
        DevUiPathGuard.EnsureNoReparsePoints(
            coordinates.SiteManifestPath,
            _fileSystem,
            "Ticket site manifest");

        TicketSiteManifest manifest =
            await TicketSiteManifest.ReadAsync(
                coordinates.SiteManifestPath,
                ct);
        ValidateManifest(
            coordinates,
            currentPair,
            manifest);

        string indexPath =
            Path.Combine(coordinates.SiteDirectory, "index.html");
        if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException(
                "The published ticket site has no index page.",
                indexPath);
        }
        DevUiPathGuard.EnsureNoReparsePoints(
            indexPath,
            _fileSystem,
            "Published ticket site");
        RevalidateCoordinates(coordinates);
        return new ReviewSitePublication(
            coordinates,
            manifest,
            Reconstructed: false);
    }

    private void RevalidateConfiguredRoots()
    {
        DevUiPathGuard.EnsureNoReparsePoints(
            _options.CacheRoot,
            _fileSystem,
            "Dev UI cache root");
        DevUiPathGuard.EnsureSafeDescendant(
            _options.CacheRoot,
            _options.SnapshotCacheRoot,
            _fileSystem,
            "Snapshot cache root");
        DevUiPathGuard.EnsureSafeDescendant(
            _options.CacheRoot,
            _options.ReviewSitesRoot,
            _fileSystem,
            "Review-site root");
    }

    private void RevalidateCoordinates(
        ReviewSiteCoordinates coordinates)
    {
        TicketWorkflowDefinition canonical =
            _catalog.Get(coordinates.Workflow.RouteKey);
        DevUiPathGuard.ValidateSafeSegment(
            coordinates.RunId,
            "Authoring run identifier");
        string expectedPairDirectory =
            Path.Combine(
                _options.SnapshotCacheRoot,
                canonical.RouteKey,
                coordinates.RunId);
        string expectedSiteRoot =
            Path.Combine(
                _options.ReviewSitesRoot,
                canonical.RouteKey,
                coordinates.RunId);
        string expectedSiteDirectory =
            Path.Combine(
                expectedSiteRoot,
                canonical.SiteFolder);
        string expectedManifestPath =
            Path.Combine(
                expectedSiteDirectory,
                TicketSiteManifest.FileName);
        string expectedUrl =
            $"/review-sites/{Uri.EscapeDataString(canonical.RouteKey)}/{Uri.EscapeDataString(coordinates.RunId)}/{Uri.EscapeDataString(canonical.SiteFolder)}/";
        if (canonical != coordinates.Workflow ||
            !DevUiPathGuard.PathsEqual(
                coordinates.SnapshotPairDirectory,
                expectedPairDirectory) ||
            !DevUiPathGuard.PathsEqual(
                coordinates.SiteRoot,
                expectedSiteRoot) ||
            !DevUiPathGuard.PathsEqual(
                coordinates.SiteDirectory,
                expectedSiteDirectory) ||
            !DevUiPathGuard.PathsEqual(
                coordinates.SiteManifestPath,
                expectedManifestPath) ||
            !string.Equals(
                coordinates.SiteUrl,
                expectedUrl,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Review-site coordinates do not match the workflow's exact run-scoped layout.");
        }

        RevalidateConfiguredRoots();
        DevUiPathGuard.EnsureSafeDescendant(
            _options.SnapshotCacheRoot,
            coordinates.SnapshotPairDirectory,
            _fileSystem,
            "Snapshot pair directory");
        DevUiPathGuard.EnsureSafeDescendant(
            _options.ReviewSitesRoot,
            coordinates.SiteRoot,
            _fileSystem,
            "Run review-site root");
        DevUiPathGuard.EnsureSafeDescendant(
            coordinates.SiteRoot,
            coordinates.SiteDirectory,
            _fileSystem,
            "Workflow review-site directory");
        DevUiPathGuard.EnsureSafeDescendant(
            coordinates.SiteDirectory,
            coordinates.SiteManifestPath,
            _fileSystem,
            "Ticket site manifest");
    }

    private static void ValidateManifest(
        ReviewSiteCoordinates coordinates,
        VerifiedAuthoringSnapshotPair pair,
        TicketSiteManifest manifest)
    {
        string expectedSiteKind =
            coordinates.Workflow.SiteKind switch
            {
                TicketSiteKind.Discussion => "preparer",
                TicketSiteKind.Applying => "planner",
                _ => throw new InvalidOperationException(
                    $"Unsupported ticket site kind '{coordinates.Workflow.SiteKind}'."),
            };
        if (!string.Equals(
                manifest.SiteKind,
                expectedSiteKind,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.ProcessorKind,
                pair.Descriptor.ProcessorKind,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.RunId,
                coordinates.RunId,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.SnapshotId,
                pair.SnapshotId,
                StringComparison.Ordinal) ||
            manifest.AuthoringEpoch !=
                pair.Descriptor.AuthoringEpoch ||
            manifest.SnapshotSequence != pair.Descriptor.Sequence ||
            manifest.SnapshotSchemaVersion !=
                pair.Descriptor.SchemaVersion ||
            manifest.SnapshotSizeBytes != pair.Descriptor.SizeBytes ||
            !string.Equals(
                manifest.SnapshotSha256,
                pair.Descriptor.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                manifest.Title,
                coordinates.Workflow.SiteTitle,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.OutputPath) ||
            !DevUiPathGuard.PathsEqual(
                manifest.OutputPath,
                coordinates.SiteDirectory) ||
            manifest.Filters is null ||
            manifest.Filters.Spec is not null ||
            manifest.Filters.Project is not null ||
            manifest.Filters.Wg is not null)
        {
            throw new InvalidOperationException(
                "Ticket site manifest does not match its workflow-bound snapshot and exact run-scoped output coordinates.");
        }
    }

    private static string[] ParseStaticPathSegments(
        string subpath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subpath);
        if (subpath.Contains('\\'))
        {
            throw new ArgumentException(
                "Review-site request paths cannot contain backslashes.",
                nameof(subpath));
        }

        string relativePath = subpath.StartsWith('/')
            ? subpath[1..]
            : subpath;
        string[] encodedSegments = relativePath.Split('/');
        if (encodedSegments.Length > 0 &&
            encodedSegments[^1].Length == 0)
        {
            encodedSegments = encodedSegments[..^1];
        }
        if (encodedSegments.Length == 0 ||
            encodedSegments.Any(segment => segment.Length == 0))
        {
            throw new ArgumentException(
                "Review-site request paths must contain non-empty segments.",
                nameof(subpath));
        }

        string[] segments = new string[encodedSegments.Length];
        for (int index = 0;
             index < encodedSegments.Length;
             index++)
        {
            string segment =
                Uri.UnescapeDataString(encodedSegments[index]);
            DevUiPathGuard.ValidateSafeSegment(
                segment,
                "Review-site request segment");
            segments[index] = segment;
        }
        return segments;
    }
}

public sealed class ReviewSiteFileProvider
    : IFileProvider, IDisposable
{
    private readonly ReviewSiteStore _siteStore;
    private readonly PhysicalFileProvider _physicalProvider;

    public ReviewSiteFileProvider(ReviewSiteStore siteStore)
    {
        _siteStore = siteStore ??
            throw new ArgumentNullException(nameof(siteStore));
        _physicalProvider =
            new PhysicalFileProvider(siteStore.ReviewSitesRoot);
    }

    public IFileInfo GetFileInfo(string subpath) =>
        _siteStore.IsStaticPathAllowed(subpath)
            ? _physicalProvider.GetFileInfo(subpath)
            : new NotFoundFileInfo(subpath);

    public IDirectoryContents GetDirectoryContents(
        string subpath) =>
        _siteStore.IsStaticPathAllowed(subpath)
            ? _physicalProvider.GetDirectoryContents(subpath)
            : NotFoundDirectoryContents.Singleton;

    public IChangeToken Watch(string filter) =>
        NullChangeToken.Singleton;

    public void Dispose() => _physicalProvider.Dispose();
}
