using FhirAugury.DevUi.Configuration;
using FhirAugury.DevUi.Services;
using FhirAugury.Processing.Client;
using FhirAugury.Publishing.Tickets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FhirAugury.DevUi.Tests;

public sealed class DevUiOptionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"fhir-augury-devui-options-{Guid.NewGuid():N}");

    public DevUiOptionsTests() =>
        Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void DefaultsResolveToSeparateChildrenOfApprovedCacheRoot()
    {
        string contentRoot =
            Path.Combine(_root, "src", "FhirAugury.DevUi");
        Directory.CreateDirectory(contentRoot);
        DevUiOptions options = new();
        DevUiOptionsValidator validator = new(
            contentRoot,
            _root,
            new PhysicalFileSystemInspector());

        ValidateOptionsResult result =
            validator.Validate(null, options);

        Assert.Same(ValidateOptionsResult.Success, result);
        Assert.Equal(
            Path.Combine(_root, "cache"),
            options.CacheRoot,
            ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal(
            Path.Combine(
                _root,
                "cache",
                "devui-authoring-snapshots"),
            options.SnapshotCacheRoot,
            ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal(
            Path.Combine(
                _root,
                "cache",
                "devui-review-sites"),
            options.ReviewSitesRoot,
            ignoreCase: OperatingSystem.IsWindows());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void RejectsInvalidRecentRunLimit(int limit)
    {
        DevUiOptions options = ValidOptions();
        options.RecentRunLimit = limit;

        ValidateOptionsResult result = Validator().Validate(
            null,
            options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains(
                "RecentRunLimit",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonpositivePollingInterval(int seconds)
    {
        DevUiOptions options = ValidOptions();
        options.RunPollInterval =
            TimeSpan.FromSeconds(seconds);

        ValidateOptionsResult result = Validator().Validate(
            null,
            options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains(
                "RunPollInterval",
                StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsRepositoryContentDriveAndWebRoots()
    {
        string contentRoot = Path.Combine(_root, "content");
        Directory.CreateDirectory(
            Path.Combine(contentRoot, "wwwroot"));
        string driveRoot =
            Path.GetPathRoot(_root)!;
        string[] forbidden =
        [
            _root,
            contentRoot,
            driveRoot,
            Path.Combine(contentRoot, "wwwroot"),
        ];

        foreach (string cacheRoot in forbidden)
        {
            DevUiOptions options = ValidOptions(cacheRoot);
            ValidateOptionsResult result = new DevUiOptionsValidator(
                    contentRoot,
                    _root,
                    new PhysicalFileSystemInspector())
                .Validate(null, options);

            Assert.False(result.Succeeded);
        }
    }

    [Fact]
    public void RejectsEscapingAndOverlappingChildRoots()
    {
        DevUiOptions escaping = ValidOptions();
        escaping.SnapshotCacheRoot =
            Path.Combine(_root, "outside");
        ValidateOptionsResult escapeResult =
            Validator().Validate(null, escaping);

        DevUiOptions overlap = ValidOptions();
        overlap.SnapshotCacheRoot = "artifacts";
        overlap.ReviewSitesRoot =
            Path.Combine("artifacts", "sites");
        ValidateOptionsResult overlapResult =
            Validator().Validate(null, overlap);

        Assert.False(escapeResult.Succeeded);
        Assert.Contains(
            escapeResult.Failures ?? [],
            failure => failure.Contains(
                "must be a child",
                StringComparison.OrdinalIgnoreCase));
        Assert.False(overlapResult.Succeeded);
        Assert.Contains(
            overlapResult.Failures ?? [],
            failure => failure.Contains(
                "contain one another",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WindowsContainmentComparisonIsCaseInsensitive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string cache = Path.Combine(_root, "CaseCache");
        DevUiOptions options = ValidOptions(
            cache.ToUpperInvariant());
        options.SnapshotCacheRoot =
            Path.Combine(
                cache.ToLowerInvariant(),
                "snapshots");
        options.ReviewSitesRoot =
            Path.Combine(
                cache.ToLowerInvariant(),
                "sites");

        ValidateOptionsResult result =
            Validator().Validate(null, options);

        Assert.True(
            result.Succeeded,
            string.Join(
                Environment.NewLine,
                result.Failures ?? []));
    }

    [Fact]
    public void RejectsExistingReparsePointInPath()
    {
        DevUiOptions options = ValidOptions();
        string reparsePoint =
            Path.Combine(options.CacheRoot, "snapshots");
        FakeFileSystemInspector fileSystem = new(reparsePoint);
        DevUiOptionsValidator validator = new(
            Path.Combine(_root, "content"),
            _root,
            fileSystem);

        ValidateOptionsResult result =
            validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains(
                "reparse point",
                StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("safe:stream")]
    [InlineData("trailing.")]
    public void RejectsUnsafeWindowsPathSegments(string segment)
    {
        DevUiOptions options = ValidOptions();
        options.ReviewSitesRoot = segment;

        ValidateOptionsResult result =
            Validator().Validate(null, options);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData("//?/C:/fhir-augury-devui-cache")]
    [InlineData("//./C:/fhir-augury-devui-cache")]
    public void RejectsForwardSlashWindowsDeviceNamespaces(
        string cacheRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DevUiOptions options = new()
        {
            CacheRoot = cacheRoot,
            SnapshotCacheRoot = "snapshots",
            ReviewSitesRoot = "sites",
        };

        ValidateOptionsResult result =
            Validator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains(
                "device namespace",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RegistersStatefulServicesWithCircuitSafeLifetimes()
    {
        ServiceCollection services = new();
        ConfigurationManager configuration = new();
        TestHostEnvironment environment = new()
        {
            ContentRootPath = Path.Combine(_root, "content"),
        };

        services.AddLogging();
        services.AddDevUiOperations(
            configuration,
            environment);

        Assert.Equal(
            ServiceLifetime.Scoped,
            Lifetime<TicketOperationsService>(services));
        Assert.Equal(
            ServiceLifetime.Transient,
            Lifetime<RunPollingSession>(services));
        Assert.Equal(
            ServiceLifetime.Transient,
            Lifetime<IAuthoringControlClient>(services));
        Assert.Equal(
            ServiceLifetime.Singleton,
            Lifetime<ITicketSitePublisher>(services));
        Assert.Equal(
            ServiceLifetime.Singleton,
            Lifetime<IReviewSiteStore>(services));
    }

    private DevUiOptionsValidator Validator() => new(
        Path.Combine(_root, "content"),
        _root,
        new PhysicalFileSystemInspector());

    private DevUiOptions ValidOptions(string? cacheRoot = null)
    {
        string cache = cacheRoot ??
            Path.Combine(_root, "cache");
        return new DevUiOptions
        {
            CacheRoot = cache,
            SnapshotCacheRoot =
                Path.Combine(cache, "snapshots"),
            ReviewSitesRoot =
                Path.Combine(cache, "sites"),
        };
    }

    private static ServiceLifetime Lifetime<T>(
        IServiceCollection services) =>
        services.Last(descriptor =>
            descriptor.ServiceType == typeof(T)).Lifetime;

    private sealed class FakeFileSystemInspector(
        string reparsePoint) : IFileSystemInspector
    {
        public bool Exists(string path) =>
            DevUiPathGuard.PathsEqual(path, reparsePoint);

        public FileAttributes GetAttributes(string path) =>
            FileAttributes.Directory |
            FileAttributes.ReparsePoint;

        public void CreateDirectory(string path)
        {
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } =
            Environments.Development;

        public string ApplicationName { get; set; } =
            "FhirAugury.DevUi.Tests";

        public string ContentRootPath { get; set; } = "";

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
