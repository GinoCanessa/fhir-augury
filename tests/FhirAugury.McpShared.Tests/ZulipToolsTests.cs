using FhirAugury.McpShared.Tools;

namespace FhirAugury.McpShared.Tests;

public class ZulipToolsTests
{
    [Fact]
    public async Task GetZulipThread_ReturnsFormattedThread()
    {
        string json = """
            {
                "stream": "general",
                "topic": "test-topic",
                "messages": [
                    { "sender": "User1", "content": "Hello world", "timestamp": "2024-01-01T00:00:00Z" },
                    { "sender": "User2", "content": "Reply message", "timestamp": "2024-01-01T00:01:00Z" }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("zulip", json);

        string result = await ZulipTools.GetZulipThread(factory, "general", "test-topic");

        Assert.Contains("general > test-topic", result);
        Assert.Contains("User1", result);
        Assert.Contains("Hello world", result);
        Assert.Contains("User2", result);
        Assert.Contains("**Messages:** 2", result);
    }

    [Fact]
    public async Task GetZulipThread_NoMessages_ReturnsMessage()
    {
        string json = """
            {
                "stream": "general",
                "topic": "empty-topic",
                "messages": []
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("zulip", json);

        string result = await ZulipTools.GetZulipThread(factory, "general", "empty-topic");

        Assert.Contains("No messages found", result);
    }

    [Fact]
    public async Task ListZulipStreams_ReturnsFormattedStreams()
    {
        string json = """
            {
                "streams": [
                    { "name": "general", "description": "General discussion", "messageCount": 500 },
                    { "name": "committers", "description": "Committer chat", "messageCount": 200 }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("zulip", json);

        string result = await ZulipTools.ListZulipStreams(factory);

        Assert.Contains("Zulip Streams", result);
        Assert.Contains("general", result);
        Assert.Contains("committers", result);
        Assert.Contains("500 messages", result);
    }

    [Fact]
    public async Task ListZulipTopics_ReturnsFormattedTopics()
    {
        string json = """
            {
                "topics": [
                    { "topic": "topic-1", "messageCount": 10, "lastMessageAt": "2024-01-01T00:00:00Z" }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("zulip", json);

        string result = await ZulipTools.ListZulipTopics(factory, "general");

        Assert.Contains("Topics in general", result);
        Assert.Contains("topic-1", result);
        Assert.Contains("10 messages", result);
    }
}
