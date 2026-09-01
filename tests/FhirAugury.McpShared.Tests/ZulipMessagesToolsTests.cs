using System.Text.Json;
using FhirAugury.McpShared.Tools;

namespace FhirAugury.McpShared.Tests;

public class ZulipMessagesToolsTests
{
    [Fact]
    public async Task QueryZulipMessages_PostsQueryAndReturnsJson()
    {
        string responseJson = """
            {
                "total": 1,
                "results": [
                    { "streamName": "implementers", "topic": "R5", "senderName": "Alice" }
                ]
            }
            """;
        (IHttpClientFactory factory, MockHttpHandler handler) =
            McpTestHelper.CreateFactoryWithCapture("orchestrator", responseJson);

        string result = await ZulipMessagesTools.QueryZulipMessages(
            factory, streams: "implementers", senders: "Alice", topic: "R5");

        // Request shape: orchestrator-routed POST to the proxy query route.
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/v1/zulip/query", handler.LastRequest.RequestUri!.AbsolutePath);

        // Request body: camelCase fields matching ZulipQueryRequest, filters + defaults.
        Assert.NotNull(handler.LastRequestBody);
        using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
        JsonElement root = body.RootElement;

        JsonElement streamNames = root.GetProperty("streamNames");
        Assert.Equal(JsonValueKind.Array, streamNames.ValueKind);
        Assert.Equal("implementers", Assert.Single(streamNames.EnumerateArray()).GetString());

        JsonElement senderNames = root.GetProperty("senderNames");
        Assert.Equal(JsonValueKind.Array, senderNames.ValueKind);
        Assert.Equal("Alice", Assert.Single(senderNames.EnumerateArray()).GetString());

        Assert.Equal("R5", root.GetProperty("topic").GetString());
        Assert.Equal("timestamp", root.GetProperty("sortBy").GetString());
        Assert.Equal("desc", root.GetProperty("sortOrder").GetString());
        Assert.Equal(20, root.GetProperty("limit").GetInt32());

        // Response: raw JSON fence echoing the payload.
        Assert.Contains("```json", result);
        Assert.Contains("implementers", result);
    }
}
