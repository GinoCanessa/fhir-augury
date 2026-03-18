using FhirAugury.Database.Records;

namespace FhirAugury.Database.Tests;

public class SyncStateRecordTests
{
    [Fact]
    public void Insert_And_SelectBySourceName()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var state = new SyncStateRecord
        {
            Id = SyncStateRecord.GetIndex(),
            SourceName = "jira",
            SubSource = null,
            LastSyncAt = DateTimeOffset.UtcNow,
            LastCursor = null,
            ItemsIngested = 100,
            SyncSchedule = "01:00:00",
            NextScheduledAt = null,
            Status = "completed",
            LastError = null,
        };

        SyncStateRecord.Insert(conn, state);

        var found = SyncStateRecord.SelectSingle(conn, SourceName: "jira");
        Assert.NotNull(found);
        Assert.Equal("jira", found.SourceName);
        Assert.Equal(100, found.ItemsIngested);
        Assert.Equal("completed", found.Status);
    }

    [Fact]
    public void Update_ModifiesSyncState()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var state = new SyncStateRecord
        {
            Id = SyncStateRecord.GetIndex(),
            SourceName = "jira",
            SubSource = null,
            LastSyncAt = DateTimeOffset.UtcNow.AddHours(-1),
            LastCursor = null,
            ItemsIngested = 50,
            SyncSchedule = null,
            NextScheduledAt = null,
            Status = "completed",
            LastError = null,
        };

        SyncStateRecord.Insert(conn, state);

        state.LastSyncAt = DateTimeOffset.UtcNow;
        state.ItemsIngested = 150;
        SyncStateRecord.Update(conn, state);

        var found = SyncStateRecord.SelectSingle(conn, SourceName: "jira");
        Assert.NotNull(found);
        Assert.Equal(150, found.ItemsIngested);
    }
}
