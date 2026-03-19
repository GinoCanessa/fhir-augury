using FhirAugury.Models.Caching;

namespace FhirAugury.Sources.Tests.Caching;

public class CacheMetadataTests : IDisposable
{
    private readonly string _tempDir;

    public CacheMetadataTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fhir-augury-meta-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task JiraMetadata_RoundTrip()
    {
        var meta = new JiraCacheMetadata
        {
            LastSyncDate = "2026-03-18",
            LastSyncTimestamp = new DateTimeOffset(2026, 3, 18, 12, 0, 0, TimeSpan.Zero),
            TotalFiles = 42,
            Format = "json",
        };

        await CacheMetadataService.WriteMetadataAsync(_tempDir, "_meta_jira.json", meta, CancellationToken.None);
        var read = CacheMetadataService.ReadMetadata<JiraCacheMetadata>(_tempDir, "_meta_jira.json");

        Assert.NotNull(read);
        Assert.Equal("2026-03-18", read.LastSyncDate);
        Assert.Equal(42, read.TotalFiles);
        Assert.Equal("json", read.Format);
        Assert.Equal(meta.LastSyncTimestamp, read.LastSyncTimestamp);
    }

    [Fact]
    public async Task ZulipStreamMetadata_RoundTrip()
    {
        var meta = new ZulipStreamCacheMetadata
        {
            StreamId = 270,
            StreamName = "implementers",
            LastSyncDate = "2026-03-18",
            LastSyncTimestamp = DateTimeOffset.UtcNow,
            InitialDownloadComplete = true,
        };

        await CacheMetadataService.WriteMetadataAsync(_tempDir, "_meta_s270.json", meta, CancellationToken.None);
        var read = CacheMetadataService.ReadMetadata<ZulipStreamCacheMetadata>(_tempDir, "_meta_s270.json");

        Assert.NotNull(read);
        Assert.Equal(270, read.StreamId);
        Assert.Equal("implementers", read.StreamName);
        Assert.True(read.InitialDownloadComplete);
    }

    [Fact]
    public async Task ConfluenceMetadata_RoundTrip()
    {
        var meta = new ConfluenceCacheMetadata
        {
            LastSyncDate = "2026-03-18",
            LastSyncTimestamp = DateTimeOffset.UtcNow,
            TotalFiles = 100,
            Format = "json",
        };

        await CacheMetadataService.WriteMetadataAsync(_tempDir, "_meta_confluence.json", meta, CancellationToken.None);
        var read = CacheMetadataService.ReadMetadata<ConfluenceCacheMetadata>(_tempDir, "_meta_confluence.json");

        Assert.NotNull(read);
        Assert.Equal(100, read.TotalFiles);
        Assert.Equal("json", read.Format);
    }

    [Fact]
    public void ReadMetadata_MissingFile_ReturnsNull()
    {
        var result = CacheMetadataService.ReadMetadata<JiraCacheMetadata>(_tempDir, "nonexistent.json");
        Assert.Null(result);
    }

    [Fact]
    public async Task WriteMetadata_CreatesDirectories()
    {
        var nested = Path.Combine(_tempDir, "nested", "path");
        var meta = new JiraCacheMetadata { TotalFiles = 1 };

        await CacheMetadataService.WriteMetadataAsync(nested, "test.json", meta, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(nested, "test.json")));
    }
}
