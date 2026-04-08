using FhirAugury.Source.Zulip.Database.Records;
using FhirAugury.Source.Zulip.Ingestion;
using zulip_cs_lib.Models;

namespace FhirAugury.Source.Zulip.Tests;

public class ZulipMessageMapperTests
{
    [Fact]
    public void MapStream_FromStreamObject_MapsAllFields()
    {
        StreamObject so = new()
        {
            StreamId = 42,
            Name = "implementers",
            Description = "Discussion for implementers",
            IsWebPublic = true,
        };

        ZulipStreamRecord record = ZulipMessageMapper.MapStream(so);

        Assert.Equal(42, record.ZulipStreamId);
        Assert.Equal("implementers", record.Name);
        Assert.Equal("Discussion for implementers", record.Description);
        Assert.True(record.IsWebPublic);
        Assert.Equal(0, record.MessageCount);
        Assert.True(record.IncludeStream);
        Assert.Equal(5, record.BaselineValue);
    }

    [Fact]
    public void MapStream_NullIsWebPublic_DefaultsToFalse()
    {
        StreamObject so = new()
        {
            StreamId = 10,
            Name = "test-stream",
            IsWebPublic = null,
        };

        ZulipStreamRecord record = ZulipMessageMapper.MapStream(so);

        Assert.False(record.IsWebPublic);
    }

    [Fact]
    public void MapStream_NullName_DefaultsToEmpty()
    {
        StreamObject so = new()
        {
            StreamId = 10,
            Name = null!,
        };

        ZulipStreamRecord record = ZulipMessageMapper.MapStream(so);

        Assert.Equal(string.Empty, record.Name);
    }

    [Fact]
    public void MapMessage_FromMessageObject_MapsAllFields()
    {
        MessageObject mo = new()
        {
            Id = 1001,
            StreamId = 42,
            Subject = "R5 ballot",
            SenderId = 100,
            SenderFullName = "Alice",
            SenderEmail = "alice@example.com",
            Content = "<p>Hello world</p>",
            Timestamp = 1700000000,
        };

        ZulipMessageRecord record = ZulipMessageMapper.MapMessage(mo, "general", 1);

        Assert.Equal(1001, record.ZulipMessageId);
        Assert.Equal(1, record.StreamId);
        Assert.Equal("general", record.StreamName);
        Assert.Equal("R5 ballot", record.Topic);
        Assert.Equal(100, record.SenderId);
        Assert.Equal("Alice", record.SenderName);
        Assert.Equal("alice@example.com", record.SenderEmail);
        Assert.Equal("<p>Hello world</p>", record.ContentHtml);
        Assert.Equal("Hello world", record.ContentPlain);
        Assert.Null(record.Reactions);
    }

    [Fact]
    public void MapMessage_NullContent_ProducesEmptyPlainText()
    {
        MessageObject mo = new()
        {
            Id = 2,
            Content = null!,
            Subject = "test",
            SenderFullName = "Bob",
            Timestamp = 1700000000,
        };

        ZulipMessageRecord record = ZulipMessageMapper.MapMessage(mo, "stream", 1);

        Assert.Equal(string.Empty, record.ContentPlain);
    }

    [Fact]
    public void MapMessage_NullSenderFullName_DefaultsToUnknown()
    {
        MessageObject mo = new()
        {
            Id = 3,
            Content = "text",
            Subject = "test",
            SenderFullName = null!,
            Timestamp = 1700000000,
        };

        ZulipMessageRecord record = ZulipMessageMapper.MapMessage(mo, "stream", 1);

        Assert.Equal("Unknown", record.SenderName);
    }

    [Fact]
    public void MapMessage_NullSubject_DefaultsToEmpty()
    {
        MessageObject mo = new()
        {
            Id = 4,
            Content = "text",
            Subject = null!,
            SenderFullName = "Alice",
            Timestamp = 1700000000,
        };

        ZulipMessageRecord record = ZulipMessageMapper.MapMessage(mo, "stream", 1);

        Assert.Equal(string.Empty, record.Topic);
    }

    [Fact]
    public void MapMessage_TimestampMapsCorrectly()
    {
        MessageObject mo = new()
        {
            Id = 5,
            Content = "text",
            Subject = "test",
            SenderFullName = "Alice",
            Timestamp = 1700000000, // 2023-11-14T22:13:20Z
        };

        ZulipMessageRecord record = ZulipMessageMapper.MapMessage(mo, "stream", 1);

        DateTimeOffset expected = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        Assert.Equal(expected, record.Timestamp);
        Assert.Equal(expected, record.CreatedAt);
    }
}
