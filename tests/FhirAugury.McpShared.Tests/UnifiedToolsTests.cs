using FhirAugury.McpShared.Tools;

namespace FhirAugury.McpShared.Tests;

public class UnifiedToolsTests
{
    [Fact]
    public async Task GetStats_ReturnsFormattedStatus()
    {
        string json = """
            {
                "services": [
                    { "name": "jira", "status": "healthy", "itemCount": 1000, "dbSizeBytes": 10000000 },
                    { "name": "zulip", "status": "healthy", "itemCount": 5000, "dbSizeBytes": 50000000 }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await UnifiedTools.GetStats(factory);

        Assert.Contains("Services Status", result);
        Assert.Contains("jira", result);
        Assert.Contains("zulip", result);
    }

    [Fact]
    public async Task TriggerSync_ReturnsFormattedStatus()
    {
        string json = """
            {
                "statuses": [
                    { "source": "jira", "status": "started", "message": "Incremental sync started" }
                ]
            }
            """;
        IHttpClientFactory factory = McpTestHelper.CreateFactory("orchestrator", json);

        string result = await UnifiedTools.TriggerSync(factory);

        Assert.Contains("Sync Triggered", result);
        Assert.Contains("jira", result);
        Assert.Contains("started", result);
    }
}
