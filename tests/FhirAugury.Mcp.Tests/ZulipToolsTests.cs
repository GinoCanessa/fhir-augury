using Fhiraugury;
using FhirAugury.Mcp.Tools;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Testing;
using NSubstitute;

namespace FhirAugury.Mcp.Tests;

public class ZulipToolsTests
{
    [Fact]
    public async Task SearchZulip_ReturnsFormattedResults()
    {
        var mockResponse = McpTestHelper.CreateSearchResponse(
            ("zulip", "general:test-topic", "Test Topic", 0.9));

        var mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(mockResponse),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        var client = Substitute.For<SourceService.SourceServiceClient>();
        client.SearchAsync(Arg.Any<SearchRequest>(), null, null, default)
            .Returns(mockCall);

        var result = await ZulipTools.SearchZulip(client, "test query");

        Assert.Contains("Search Results", result);
        Assert.Contains("test-topic", result);
    }

    [Fact]
    public async Task GetZulipThread_ReturnsFormattedThread()
    {
        var thread = new ZulipThread { StreamName = "general", Topic = "test-topic" };
        thread.Messages.Add(new ZulipMessage
        {
            Id = 1, StreamName = "general", Topic = "test-topic",
            SenderName = "User1", Content = "Hello world",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        thread.Messages.Add(new ZulipMessage
        {
            Id = 2, StreamName = "general", Topic = "test-topic",
            SenderName = "User2", Content = "Reply message",
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        var mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(thread),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        var client = Substitute.For<ZulipService.ZulipServiceClient>();
        client.GetThreadAsync(Arg.Any<ZulipGetThreadRequest>(), null, null, default)
            .Returns(mockCall);

        var result = await ZulipTools.GetZulipThread(client, "general", "test-topic");

        Assert.Contains("general > test-topic", result);
        Assert.Contains("User1", result);
        Assert.Contains("Hello world", result);
        Assert.Contains("User2", result);
        Assert.Contains("**Messages:** 2", result);
    }

    [Fact]
    public async Task GetZulipThread_NoMessages_ReturnsMessage()
    {
        var thread = new ZulipThread { StreamName = "general", Topic = "empty-topic" };

        var mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(thread),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        var client = Substitute.For<ZulipService.ZulipServiceClient>();
        client.GetThreadAsync(Arg.Any<ZulipGetThreadRequest>(), null, null, default)
            .Returns(mockCall);

        var result = await ZulipTools.GetZulipThread(client, "general", "empty-topic");

        Assert.Contains("No messages found", result);
    }

    [Fact]
    public async Task ListZulipStreams_ReturnsFormattedStreams()
    {
        var streams = new[]
        {
            new ZulipStream { Id = 1, Name = "general", Description = "General discussion", MessageCount = 500 },
            new ZulipStream { Id = 2, Name = "committers", Description = "Committer chat", MessageCount = 200 },
        };

        var streamCall = McpTestHelper.CreateStreamingCall(streams);
        var client = Substitute.For<ZulipService.ZulipServiceClient>();
        client.ListStreams(Arg.Any<ZulipListStreamsRequest>(), null, null, default)
            .Returns(streamCall);

        var result = await ZulipTools.ListZulipStreams(client);

        Assert.Contains("Zulip Streams", result);
        Assert.Contains("general", result);
        Assert.Contains("committers", result);
        Assert.Contains("500 messages", result);
    }

    [Fact]
    public async Task ListZulipTopics_ReturnsFormattedTopics()
    {
        var topics = new[]
        {
            new ZulipTopic
            {
                StreamName = "general", Topic = "topic-1", MessageCount = 10,
                LastMessageAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

        var streamCall = McpTestHelper.CreateStreamingCall(topics);
        var client = Substitute.For<ZulipService.ZulipServiceClient>();
        client.ListTopics(Arg.Any<ZulipListTopicsRequest>(), null, null, default)
            .Returns(streamCall);

        var result = await ZulipTools.ListZulipTopics(client, "general");

        Assert.Contains("Topics in general", result);
        Assert.Contains("topic-1", result);
        Assert.Contains("10 messages", result);
    }

    [Fact]
    public async Task SnapshotZulipThread_ReturnsMarkdown()
    {
        var mockResponse = new SnapshotResponse
        {
            Id = "general:test-topic",
            Source = "zulip",
            Markdown = "# general > test-topic\n\nFull thread content...",
        };

        var mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(mockResponse),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        var client = Substitute.For<ZulipService.ZulipServiceClient>();
        client.GetThreadSnapshotAsync(Arg.Any<ZulipSnapshotRequest>(), null, null, default)
            .Returns(mockCall);

        var result = await ZulipTools.SnapshotZulipThread(client, "general", "test-topic");

        Assert.Contains("general > test-topic", result);
    }
}
