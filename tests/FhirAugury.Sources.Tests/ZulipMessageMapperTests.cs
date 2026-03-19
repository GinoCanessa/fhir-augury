using System.Text.Json;
using FhirAugury.Sources.Zulip;

namespace FhirAugury.Sources.Tests;

public class ZulipMessageMapperTests
{
    private static readonly string TestDataPath = Path.Combine("TestData", "sample-zulip-messages.json");

    private JsonElement LoadTestData()
    {
        var json = File.ReadAllText(TestDataPath);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void MapStream_ExtractsAllFields()
    {
        var root = LoadTestData();
        var streamJson = root.GetProperty("streams")[0];

        var record = ZulipMessageMapper.MapStream(streamJson);

        Assert.Equal(10, record.ZulipStreamId);
        Assert.Equal("implementers", record.Name);
        Assert.Equal("Discussion of FHIR implementation topics", record.Description);
        Assert.True(record.IsWebPublic);
    }

    [Fact]
    public void MapStream_PrivateStream_IsWebPublicFalse()
    {
        var root = LoadTestData();
        var streamJson = root.GetProperty("streams")[2];

        var record = ZulipMessageMapper.MapStream(streamJson);

        Assert.Equal(30, record.ZulipStreamId);
        Assert.Equal("private-stream", record.Name);
        Assert.False(record.IsWebPublic);
    }

    [Fact]
    public void MapMessage_ExtractsAllFields()
    {
        var root = LoadTestData();
        var msgJson = root.GetProperty("messages")[0];

        var record = ZulipMessageMapper.MapMessage(msgJson, "implementers", 1);

        Assert.Equal(100001, record.ZulipMessageId);
        Assert.Equal(1, record.StreamId);
        Assert.Equal("implementers", record.StreamName);
        Assert.Equal("FHIRPath aggregates", record.Topic);
        Assert.Equal(42, record.SenderId);
        Assert.Equal("Alice Smith", record.SenderName);
        Assert.Equal("alice@example.com", record.SenderEmail);
    }

    [Fact]
    public void MapMessage_StripsHtmlContent()
    {
        var root = LoadTestData();
        var msgJson = root.GetProperty("messages")[0];

        var record = ZulipMessageMapper.MapMessage(msgJson, "implementers", 1);

        Assert.DoesNotContain("<p>", record.ContentPlain);
        Assert.DoesNotContain("<strong>", record.ContentPlain);
        Assert.Contains("FHIRPath", record.ContentPlain);
        Assert.Contains("aggregate functions", record.ContentPlain);
    }

    [Fact]
    public void MapMessage_StripsCodeTags()
    {
        var root = LoadTestData();
        var msgJson = root.GetProperty("messages")[1];

        var record = ZulipMessageMapper.MapMessage(msgJson, "implementers", 1);

        Assert.DoesNotContain("<code>", record.ContentPlain);
        Assert.Contains("aggregate()", record.ContentPlain);
    }

    [Fact]
    public void MapMessage_StripsLinkTags()
    {
        var root = LoadTestData();
        var msgJson = root.GetProperty("messages")[2];

        var record = ZulipMessageMapper.MapMessage(msgJson, "implementers", 1);

        Assert.DoesNotContain("<a ", record.ContentPlain);
        Assert.Contains("tutorial", record.ContentPlain);
    }

    [Fact]
    public void MapMessage_ConvertsTimestamp()
    {
        var root = LoadTestData();
        var msgJson = root.GetProperty("messages")[0];

        var record = ZulipMessageMapper.MapMessage(msgJson, "implementers", 1);

        // Unix timestamp 1710000000 = 2024-03-09T16:00:00Z
        Assert.Equal(2024, record.Timestamp.Year);
    }

    [Fact]
    public void MapMessage_SerializesReactions()
    {
        var root = LoadTestData();
        var msgJson = root.GetProperty("messages")[0];

        var record = ZulipMessageMapper.MapMessage(msgJson, "implementers", 1);

        Assert.NotNull(record.Reactions);
        Assert.Contains("+1", record.Reactions);
    }

    [Fact]
    public void MapMessage_EmptyReactions_ReturnsNull()
    {
        var root = LoadTestData();
        var msgJson = root.GetProperty("messages")[1];

        var record = ZulipMessageMapper.MapMessage(msgJson, "implementers", 1);

        Assert.Null(record.Reactions);
    }

    [Fact]
    public void MapMessage_NullSenderEmail_HandlesGracefully()
    {
        var root = LoadTestData();
        var msgJson = root.GetProperty("messages")[2];

        var record = ZulipMessageMapper.MapMessage(msgJson, "implementers", 1);

        Assert.Null(record.SenderEmail);
        Assert.Equal("Carol White", record.SenderName);
    }
}
