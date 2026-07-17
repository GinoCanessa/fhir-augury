using FhirAugury.McpShared.Tools;

namespace FhirAugury.McpShared.Tests;

public class ZulipThreadsToolsTests
{
    [Fact]
    public async Task GetZulipThread_GetsThreadRouteAndReturnsJson()
    {
        string responseJson = """
            {
                "streamName": "general",
                "topic": "test-topic",
                "messages": [ { "sender": "User1", "content": "Hello world" } ]
            }
            """;
        (IHttpClientFactory factory, MockHttpHandler handler) =
            McpTestHelper.CreateFactoryWithCapture("orchestrator", responseJson);

        string result = await ZulipThreadsTools.GetZulipThread(factory, "general", "test-topic");

        // Canonical behavior: orchestrator-routed GET to the thread route (stream/topic
        // carried as query-string parameters), raw-JSON output.
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/api/v1/zulip/threads", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("streamName=general", handler.LastRequest.RequestUri.Query);
        Assert.Contains("topic=test-topic", handler.LastRequest.RequestUri.Query);
        Assert.Contains("```json", result);
        Assert.Contains("Hello world", result);
    }
}
