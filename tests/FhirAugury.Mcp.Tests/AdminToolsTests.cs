using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Mcp.Tools;

namespace FhirAugury.Mcp.Tests;

public class AdminToolsTests : IDisposable
{
    private readonly DatabaseService _db;
    private readonly string _dbPath;

    public AdminToolsTests()
    {
        (_db, _dbPath) = McpTestHelper.CreateTempDatabaseService();
        SeedData();
    }

    private void SeedData()
    {
        using var conn = _db.OpenConnection();

        var issue = McpTestHelper.CreateSampleIssue("FHIR-60001", "Test issue for stats");
        JiraIssueRecord.Insert(conn, issue);

        var stream = McpTestHelper.CreateSampleStream(40, "stats-stream");
        ZulipStreamRecord.Insert(conn, stream);
        var msg = McpTestHelper.CreateSampleMessage(500, stream.Id, "stats-stream", "stats-topic",
            "Ivy", "Message for stats test");
        ZulipMessageRecord.Insert(conn, msg);

        var syncState = new SyncStateRecord
        {
            Id = SyncStateRecord.GetIndex(),
            SourceName = "jira",
            SubSource = null,
            LastSyncAt = DateTimeOffset.UtcNow.AddHours(-1),
            LastCursor = null,
            ItemsIngested = 100,
            SyncSchedule = "01:00:00",
            NextScheduledAt = DateTimeOffset.UtcNow.AddMinutes(30),
            Status = "idle",
            LastError = null,
        };
        SyncStateRecord.Insert(conn, syncState);
    }

    [Fact]
    public void GetStats_OverviewReturnsAllCategories()
    {
        var result = AdminTools.GetStats(_db);
        Assert.Contains("Jira Issues", result);
        Assert.Contains("Zulip Streams", result);
        Assert.Contains("Zulip Messages", result);
        Assert.Contains("Database Statistics", result);
    }

    [Fact]
    public void GetStats_SourceFilter_ReturnsSourceOnly()
    {
        var result = AdminTools.GetStats(_db, source: "jira");
        Assert.Contains("Issues", result);
        Assert.Contains("Sync State", result);
        Assert.Contains("idle", result);
    }

    [Fact]
    public void GetStats_UnknownSource_ReturnsError()
    {
        var result = AdminTools.GetStats(_db, source: "unknown");
        Assert.Contains("Unknown source", result);
    }

    [Fact]
    public void GetSyncStatus_ReturnsSyncInfo()
    {
        var result = AdminTools.GetSyncStatus(_db);
        Assert.Contains("jira", result);
        Assert.Contains("idle", result);
        Assert.Contains("100", result);
    }

    public void Dispose()
    {
        McpTestHelper.CleanupTempDb(_db, _dbPath);
    }
}
