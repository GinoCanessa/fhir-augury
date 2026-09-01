using FhirAugury.McpShared.Tools;

namespace FhirAugury.McpShared.Tests;

public class ZulipStreamsToolsTests
{
    [Fact]
    public async Task ListZulipStreams_GetsStreamsRouteAndReturnsJson()
    {
        string responseJson = """
            {
                "streams": [ { "name": "general", "messageCount": 500 } ]
            }
            """;
        (IHttpClientFactory factory, MockHttpHandler handler) =
            McpTestHelper.CreateFactoryWithCapture("orchestrator", responseJson);

        string result = await ZulipStreamsTools.ListZulipStreams(factory);

        // Canonical behavior: orchestrator-routed GET to the streams route, raw-JSON output.
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/api/v1/zulip/streams", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("```json", result);
        Assert.Contains("general", result);
    }
}
