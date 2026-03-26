using Fhiraugury;
using FhirAugury.McpShared.Tools;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Testing;
using NSubstitute;

namespace FhirAugury.McpShared.Tests;

public class ZulipToolsTests
{
    [Fact]
    public async Task SearchZulip_ReturnsFormattedResults()
    {
        SearchResponse mockResponse = McpTestHelper.CreateSearchResponse(
            ("zulip", "general:test-topic", "Test Topic", 0.9));

        AsyncUnaryCall<SearchResponse> mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(mockResponse),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        SourceService.SourceServiceClient client = Substitute.For<SourceService.SourceServiceClient>();
        client.SearchAsync(Arg.Any<SearchRequest>(), null, null, default)
            .Returns(mockCall);

        string result = await ZulipTools.SearchZulip(client, "test query");

        Assert.Contains("Search Results", result);
        Assert.Contains("test-topic", result);
    }

    [Fact]
    public async Task GetZulipThread_ReturnsFormattedThread()
    {
        ZulipThread thread = new ZulipThread { StreamName = "general", Topic = "test-topic" };
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

        AsyncUnaryCall<ZulipThread> mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(thread),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        ZulipService.ZulipServiceClient client = Substitute.For<ZulipService.ZulipServiceClient>();
        client.GetThreadAsync(Arg.Any<ZulipGetThreadRequest>(), null, null, default)
            .Returns(mockCall);

        string result = await ZulipTools.GetZulipThread(client, "general", "test-topic");

        Assert.Contains("general > test-topic", result);
        Assert.Contains("User1", result);
        Assert.Contains("Hello world", result);
        Assert.Contains("User2", result);
        Assert.Contains("**Messages:** 2", result);
    }

    [Fact]
    public async Task GetZulipThread_NoMessages_ReturnsMessage()
    {
        ZulipThread thread = new ZulipThread { StreamName = "general", Topic = "empty-topic" };

        AsyncUnaryCall<ZulipThread> mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(thread),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        ZulipService.ZulipServiceClient client = Substitute.For<ZulipService.ZulipServiceClient>();
        client.GetThreadAsync(Arg.Any<ZulipGetThreadRequest>(), null, null, default)
            .Returns(mockCall);

        string result = await ZulipTools.GetZulipThread(client, "general", "empty-topic");

        Assert.Contains("No messages found", result);
    }

    [Fact]
    public async Task ListZulipStreams_ReturnsFormattedStreams()
    {
        ZulipStream[] streams = new[]
        {
            new ZulipStream { Id = 1, Name = "general", Description = "General discussion", MessageCount = 500 },
            new ZulipStream { Id = 2, Name = "committers", Description = "Committer chat", MessageCount = 200 },
        };

        AsyncServerStreamingCall<ZulipStream> streamCall = McpTestHelper.CreateStreamingCall(streams);
        ZulipService.ZulipServiceClient client = Substitute.For<ZulipService.ZulipServiceClient>();
        client.ListStreams(Arg.Any<ZulipListStreamsRequest>(), null, null, default)
            .Returns(streamCall);

        string result = await ZulipTools.ListZulipStreams(client);

        Assert.Contains("Zulip Streams", result);
        Assert.Contains("general", result);
        Assert.Contains("committers", result);
        Assert.Contains("500 messages", result);
    }

    [Fact]
    public async Task ListZulipTopics_ReturnsFormattedTopics()
    {
        ZulipTopic[] topics = new[]
        {
            new ZulipTopic
            {
                StreamName = "general", Topic = "topic-1", MessageCount = 10,
                LastMessageAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        };

        AsyncServerStreamingCall<ZulipTopic> streamCall = McpTestHelper.CreateStreamingCall(topics);
        ZulipService.ZulipServiceClient client = Substitute.For<ZulipService.ZulipServiceClient>();
        client.ListTopics(Arg.Any<ZulipListTopicsRequest>(), null, null, default)
            .Returns(streamCall);

        string result = await ZulipTools.ListZulipTopics(client, "general");

        Assert.Contains("Topics in general", result);
        Assert.Contains("topic-1", result);
        Assert.Contains("10 messages", result);
    }

    [Fact]
    public async Task SnapshotZulipThread_ReturnsMarkdown()
    {
        SnapshotResponse mockResponse = new SnapshotResponse
        {
            Id = "general:test-topic",
            Source = "zulip",
            Markdown = "# general > test-topic\n\nFull thread content...",
        };

        AsyncUnaryCall<SnapshotResponse> mockCall = TestCalls.AsyncUnaryCall(
            Task.FromResult(mockResponse),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

        ZulipService.ZulipServiceClient client = Substitute.For<ZulipService.ZulipServiceClient>();
        client.GetThreadSnapshotAsync(Arg.Any<ZulipSnapshotRequest>(), null, null, default)
            .Returns(mockCall);

        string result = await ZulipTools.SnapshotZulipThread(client, "general", "test-topic");

        Assert.Contains("general > test-topic", result);
    }
}
