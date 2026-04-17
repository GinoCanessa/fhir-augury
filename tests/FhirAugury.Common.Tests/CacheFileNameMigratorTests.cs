using FhirAugury.Common.Caching;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Common.Tests;

public class CacheFileNameMigratorTests : IDisposable
{
    private const string Source = "jira";
    private readonly string _root;
    private readonly FileSystemResponseCache _cache;

    public CacheFileNameMigratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cfm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _cache = new FileSystemResponseCache(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task SeedAsync(string key, string content)
    {
        using MemoryStream ms = new(System.Text.Encoding.UTF8.GetBytes(content));
        await _cache.PutAsync(Source, key, ms, CancellationToken.None);
    }

    private string? Read(string key)
    {
        if (!_cache.TryGet(Source, key, out Stream? s))
            return null;
        using (s)
        using (StreamReader r = new(s))
            return r.ReadToEnd();
    }

    private bool Exists(string key)
    {
        if (!_cache.TryGet(Source, key, out Stream? s))
            return false;
        s!.Dispose();
        return true;
    }

    [Fact]
    public async Task Migrate_RenamesDayOf_ToRangeForm()
    {
        await SeedAsync("FHIR/xml/DayOf_2020-01-01-000.xml", "day-content");

        CacheFileNameMigrator.MigrationResult r = await CacheFileNameMigrator.MigrateAsync(_cache, Source, NullLogger.Instance);

        Assert.Equal(1, r.Migrated);
        Assert.Equal(0, r.AlreadyMigrated);
        Assert.Equal(0, r.Failed);
        Assert.False(Exists("FHIR/xml/DayOf_2020-01-01-000.xml"));
        Assert.Equal("day-content", Read("FHIR/xml/20200101-20200101-000.xml"));
    }

    [Fact]
    public async Task Migrate_RenamesWeekOf_WithCorrectEndDate()
    {
        // 2020-01-06 is a Monday
        await SeedAsync("stream-1/_WeekOf_2020-01-06-000.json", "week-content");

        CacheFileNameMigrator.MigrationResult r = await CacheFileNameMigrator.MigrateAsync(_cache, Source, NullLogger.Instance);

        Assert.Equal(1, r.Migrated);
        Assert.Equal("week-content", Read("stream-1/20200106-20200112-000.json"));
    }

    [Fact]
    public async Task Migrate_NoLegacyFiles_IsNoOp()
    {
        await SeedAsync("FHIR/xml/20200101-20200101-000.xml", "x");

        CacheFileNameMigrator.MigrationResult r = await CacheFileNameMigrator.MigrateAsync(_cache, Source, NullLogger.Instance);

        Assert.Equal(0, r.Migrated);
        Assert.Equal(0, r.AlreadyMigrated);
        Assert.Equal(0, r.Failed);
        Assert.Equal("x", Read("FHIR/xml/20200101-20200101-000.xml"));
    }

    [Fact]
    public async Task Migrate_IsIdempotent()
    {
        await SeedAsync("FHIR/xml/DayOf_2020-01-01-000.xml", "c");

        CacheFileNameMigrator.MigrationResult first = await CacheFileNameMigrator.MigrateAsync(_cache, Source, NullLogger.Instance);
        CacheFileNameMigrator.MigrationResult second = await CacheFileNameMigrator.MigrateAsync(_cache, Source, NullLogger.Instance);

        Assert.Equal(1, first.Migrated);
        Assert.Equal(0, second.Migrated);
        Assert.Equal(0, second.AlreadyMigrated);
        Assert.Equal(0, second.Failed);
        Assert.Equal("c", Read("FHIR/xml/20200101-20200101-000.xml"));
    }

    [Fact]
    public async Task Migrate_CrashRecovery_NewAlreadyExists()
    {
        await SeedAsync("FHIR/xml/DayOf_2020-01-01-000.xml", "old");
        await SeedAsync("FHIR/xml/20200101-20200101-000.xml", "new");

        CacheFileNameMigrator.MigrationResult r = await CacheFileNameMigrator.MigrateAsync(_cache, Source, NullLogger.Instance);

        Assert.Equal(0, r.Migrated);
        Assert.Equal(1, r.AlreadyMigrated);
        Assert.False(Exists("FHIR/xml/DayOf_2020-01-01-000.xml"));
        Assert.Equal("new", Read("FHIR/xml/20200101-20200101-000.xml"));
    }

    [Fact]
    public async Task Migrate_PreservesPrefix()
    {
        await SeedAsync("PROJ/xml/DayOf_2020-01-01-000.xml", "p");

        await CacheFileNameMigrator.MigrateAsync(_cache, Source, NullLogger.Instance);

        Assert.True(Exists("PROJ/xml/20200101-20200101-000.xml"));
        Assert.False(Exists("xml/20200101-20200101-000.xml"));
    }

    [Fact]
    public async Task Migrate_SequenceNumberRoundTrips()
    {
        await SeedAsync("FHIR/xml/DayOf_2020-01-01-042.xml", "s");

        await CacheFileNameMigrator.MigrateAsync(_cache, Source, NullLogger.Instance);

        Assert.True(Exists("FHIR/xml/20200101-20200101-042.xml"));
        Assert.Equal("s", Read("FHIR/xml/20200101-20200101-042.xml"));
    }

    [Fact]
    public async Task Migrate_MultipleFiles_BulkConvert()
    {
        await SeedAsync("FHIR/xml/DayOf_2020-01-01-000.xml", "a");
        await SeedAsync("FHIR/xml/DayOf_2020-01-02-000.xml", "b");
        await SeedAsync("FHIR/xml/DayOf_2020-01-03-000.xml", "c");

        CacheFileNameMigrator.MigrationResult r = await CacheFileNameMigrator.MigrateAsync(_cache, Source, NullLogger.Instance);

        Assert.Equal(3, r.Migrated);
        Assert.Equal("a", Read("FHIR/xml/20200101-20200101-000.xml"));
        Assert.Equal("b", Read("FHIR/xml/20200102-20200102-000.xml"));
        Assert.Equal("c", Read("FHIR/xml/20200103-20200103-000.xml"));
    }
}
