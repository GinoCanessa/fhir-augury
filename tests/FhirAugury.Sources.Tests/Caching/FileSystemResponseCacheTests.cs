using FhirAugury.Models.Caching;

namespace FhirAugury.Sources.Tests.Caching;

public class FileSystemResponseCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemResponseCache _cache;

    public FileSystemResponseCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fhir-augury-test-{Guid.NewGuid():N}");
        _cache = new FileSystemResponseCache(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task PutThenGet_RoundTrips()
    {
        var content = "hello cache"u8.ToArray();
        using var writeStream = new MemoryStream(content);
        await _cache.PutAsync("jira", "test.json", writeStream, CancellationToken.None);

        var found = _cache.TryGet("jira", "test.json", out var readStream);
        Assert.True(found);

        using (readStream)
        {
            using var ms = new MemoryStream();
            await readStream.CopyToAsync(ms);
            Assert.Equal(content, ms.ToArray());
        }
    }

    [Fact]
    public void TryGet_Missing_ReturnsFalse()
    {
        var found = _cache.TryGet("jira", "nonexistent.json", out var stream);
        Assert.False(found);
        Assert.Same(Stream.Null, stream);
    }

    [Fact]
    public async Task PutAsync_CreatesDirectories()
    {
        using var content = new MemoryStream("data"u8.ToArray());
        await _cache.PutAsync("zulip", "s270/DayOf_2026-03-18-000.json", content, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_tempDir, "zulip", "s270", "DayOf_2026-03-18-000.json")));
    }

    [Fact]
    public async Task Remove_DeletesFile()
    {
        using var content = new MemoryStream("data"u8.ToArray());
        await _cache.PutAsync("jira", "test.json", content, CancellationToken.None);

        _cache.Remove("jira", "test.json");

        Assert.False(_cache.TryGet("jira", "test.json", out _));
    }

    [Fact]
    public async Task EnumerateKeys_ReturnsAllFiles()
    {
        await PutString("jira", "DayOf_2026-03-18-000.json", "a");
        await PutString("jira", "DayOf_2026-03-18-001.json", "b");
        await PutString("jira", "DayOf_2026-03-19-000.json", "c");

        var keys = _cache.EnumerateKeys("jira").ToList();
        Assert.Equal(3, keys.Count);
    }

    [Fact]
    public async Task EnumerateKeys_ExcludesMetaFiles()
    {
        await PutString("jira", "DayOf_2026-03-18-000.json", "a");
        // Write a metadata file directly
        var metaPath = Path.Combine(_tempDir, "jira", "_meta_jira.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
        await File.WriteAllTextAsync(metaPath, "{}");

        var keys = _cache.EnumerateKeys("jira").ToList();
        Assert.Single(keys);
        Assert.DoesNotContain(keys, k => k.Contains("_meta_"));
    }

    [Fact]
    public async Task EnumerateKeys_SortsBatchFiles()
    {
        await PutString("jira", "DayOf_2026-03-20-000.xml", "c");
        await PutString("jira", "DayOf_2026-03-18-000.xml", "a");
        await PutString("jira", "_WeekOf_2026-03-18-000.xml", "b");

        var keys = _cache.EnumerateKeys("jira").ToList();

        // 2026-03-18 WeekOf before DayOf, then 2026-03-20
        Assert.Equal("_WeekOf_2026-03-18-000.xml", keys[0]);
        Assert.Equal("DayOf_2026-03-18-000.xml", keys[1]);
        Assert.Equal("DayOf_2026-03-20-000.xml", keys[2]);
    }

    [Fact]
    public async Task EnumerateKeys_WithSubPath()
    {
        await PutString("zulip", "s270/DayOf_2026-03-18-000.json", "a");
        await PutString("zulip", "s412/DayOf_2026-03-18-000.json", "b");

        var keys = _cache.EnumerateKeys("zulip", "s270").ToList();
        Assert.Single(keys);
        Assert.Contains("DayOf_2026-03-18-000.json", keys[0]);
    }

    [Fact]
    public async Task Clear_RemovesSourceDirectory()
    {
        await PutString("jira", "test.json", "a");
        await PutString("zulip", "test.json", "b");

        _cache.Clear("jira");

        Assert.False(Directory.Exists(Path.Combine(_tempDir, "jira")));
        Assert.True(_cache.TryGet("zulip", "test.json", out var s));
        s.Dispose();
    }

    [Fact]
    public async Task ClearAll_RemovesEverything()
    {
        await PutString("jira", "test.json", "a");
        await PutString("zulip", "test.json", "b");

        _cache.ClearAll();

        Assert.Empty(Directory.EnumerateFileSystemEntries(_tempDir));
    }

    [Fact]
    public async Task GetStats_ReturnsCorrectCounts()
    {
        await PutString("jira", "file1.json", "abc");
        await PutString("jira", "file2.json", "defgh");

        var stats = _cache.GetStats("jira");

        Assert.Equal("jira", stats.Source);
        Assert.Equal(2, stats.FileCount);
        Assert.Equal(8, stats.TotalBytes); // 3 + 5
    }

    [Fact]
    public void GetStats_EmptySource_ReturnsZeros()
    {
        var stats = _cache.GetStats("jira");

        Assert.Equal(0, stats.FileCount);
        Assert.Equal(0, stats.TotalBytes);
    }

    [Fact]
    public void PathTraversal_Rejected()
    {
        Assert.Throws<ArgumentException>(() => _cache.TryGet("jira", "../../etc/passwd", out _));
    }

    private async Task PutString(string source, string key, string content)
    {
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        await _cache.PutAsync(source, key, ms, CancellationToken.None);
    }
}
