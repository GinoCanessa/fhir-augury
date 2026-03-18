using FhirAugury.Database.Records;

namespace FhirAugury.Database.Tests;

public class ZulipMessageRecordTests
{
    [Fact]
    public void Insert_And_SelectSingle_RoundTrips()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream = TestHelper.CreateSampleStream(100, "implementers");
        ZulipStreamRecord.Insert(conn, stream);

        var msg = TestHelper.CreateSampleMessage(1000, stream.Id, "implementers", "FHIRPath");
        ZulipMessageRecord.Insert(conn, msg);

        var found = ZulipMessageRecord.SelectSingle(conn, ZulipMessageId: 1000);
        Assert.NotNull(found);
        Assert.Equal("implementers", found.StreamName);
        Assert.Equal("FHIRPath", found.Topic);
        Assert.Equal("Test User", found.SenderName);
    }

    [Fact]
    public void Update_ModifiesExistingRecord()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream = TestHelper.CreateSampleStream(200, "terminology");
        ZulipStreamRecord.Insert(conn, stream);

        var msg = TestHelper.CreateSampleMessage(2000, stream.Id, "terminology", "CodeSystems");
        ZulipMessageRecord.Insert(conn, msg);

        msg.ContentPlain = "Updated message content about CodeSystems";
        ZulipMessageRecord.Update(conn, msg);

        var found = ZulipMessageRecord.SelectSingle(conn, ZulipMessageId: 2000);
        Assert.NotNull(found);
        Assert.Contains("Updated", found.ContentPlain);
    }

    [Fact]
    public void Delete_RemovesRecord()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream = TestHelper.CreateSampleStream(300, "test-stream");
        ZulipStreamRecord.Insert(conn, stream);

        var msg = TestHelper.CreateSampleMessage(3000, stream.Id, "test-stream", "topic");
        ZulipMessageRecord.Insert(conn, msg);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM zulip_messages WHERE ZulipMessageId = @id";
        cmd.Parameters.AddWithValue("@id", 3000);
        cmd.ExecuteNonQuery();

        var found = ZulipMessageRecord.SelectSingle(conn, ZulipMessageId: 3000);
        Assert.Null(found);
    }

    [Fact]
    public void SelectList_ByStreamAndTopic()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream = TestHelper.CreateSampleStream(400, "implementers");
        ZulipStreamRecord.Insert(conn, stream);

        ZulipMessageRecord.Insert(conn, TestHelper.CreateSampleMessage(4001, stream.Id, "implementers", "FHIRPath", "Alice", "First message"));
        ZulipMessageRecord.Insert(conn, TestHelper.CreateSampleMessage(4002, stream.Id, "implementers", "FHIRPath", "Bob", "Second message"));
        ZulipMessageRecord.Insert(conn, TestHelper.CreateSampleMessage(4003, stream.Id, "implementers", "Other", "Carol", "Different topic"));

        var messages = ZulipMessageRecord.SelectList(conn, StreamName: "implementers", Topic: "FHIRPath");
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void SelectCount_ReturnsCorrectCount()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream = TestHelper.CreateSampleStream(500, "test");
        ZulipStreamRecord.Insert(conn, stream);

        ZulipMessageRecord.Insert(conn, TestHelper.CreateSampleMessage(5001, stream.Id, "test", "a"));
        ZulipMessageRecord.Insert(conn, TestHelper.CreateSampleMessage(5002, stream.Id, "test", "b"));

        Assert.Equal(2, ZulipMessageRecord.SelectCount(conn));
    }

    [Fact]
    public void Insert_WithIgnoreDuplicates_DoesNotThrow()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        var stream = TestHelper.CreateSampleStream(600, "test");
        ZulipStreamRecord.Insert(conn, stream);

        var msg = TestHelper.CreateSampleMessage(6000, stream.Id, "test", "topic", content: "Original");
        ZulipMessageRecord.Insert(conn, msg);

        var dup = TestHelper.CreateSampleMessage(6001, stream.Id, "test", "topic", content: "Duplicate");
        dup.Id = msg.Id;
        ZulipMessageRecord.Insert(conn, dup, ignoreDuplicates: true);

        var found = ZulipMessageRecord.SelectSingle(conn, ZulipMessageId: 6000);
        Assert.NotNull(found);
        Assert.Equal("Original", found.ContentPlain);
    }
}
