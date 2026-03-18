using FhirAugury.Database.Records;

namespace FhirAugury.Database.Tests;

public class ZulipStreamRecordTests
{
    [Fact]
    public void Insert_And_SelectSingle_RoundTrips()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream = TestHelper.CreateSampleStream(100, "implementers");
        ZulipStreamRecord.Insert(conn, stream);

        var found = ZulipStreamRecord.SelectSingle(conn, ZulipStreamId: 100);
        Assert.NotNull(found);
        Assert.Equal("implementers", found.Name);
        Assert.True(found.IsWebPublic);
    }

    [Fact]
    public void Update_ModifiesExistingRecord()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream = TestHelper.CreateSampleStream(200, "terminology");
        ZulipStreamRecord.Insert(conn, stream);

        stream.Name = "terminology-updated";
        stream.MessageCount = 42;
        ZulipStreamRecord.Update(conn, stream);

        var found = ZulipStreamRecord.SelectSingle(conn, ZulipStreamId: 200);
        Assert.NotNull(found);
        Assert.Equal("terminology-updated", found.Name);
        Assert.Equal(42, found.MessageCount);
    }

    [Fact]
    public void SelectList_ReturnsMultipleRecords()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        ZulipStreamRecord.Insert(conn, TestHelper.CreateSampleStream(300, "stream-a"));
        ZulipStreamRecord.Insert(conn, TestHelper.CreateSampleStream(301, "stream-b"));

        var all = ZulipStreamRecord.SelectList(conn);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void SelectCount_ReturnsCorrectCount()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        ZulipStreamRecord.Insert(conn, TestHelper.CreateSampleStream(400, "a"));
        ZulipStreamRecord.Insert(conn, TestHelper.CreateSampleStream(401, "b"));
        ZulipStreamRecord.Insert(conn, TestHelper.CreateSampleStream(402, "c"));

        Assert.Equal(3, ZulipStreamRecord.SelectCount(conn));
    }

    [Fact]
    public void Insert_WithIgnoreDuplicates_DoesNotThrow()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream = TestHelper.CreateSampleStream(500, "original");
        ZulipStreamRecord.Insert(conn, stream);

        // Insert again with same internal PK
        var dup = TestHelper.CreateSampleStream(501, "duplicate");
        dup.Id = stream.Id;
        ZulipStreamRecord.Insert(conn, dup, ignoreDuplicates: true);

        var found = ZulipStreamRecord.SelectSingle(conn, ZulipStreamId: 500);
        Assert.NotNull(found);
        Assert.Equal("original", found.Name);
    }
}
