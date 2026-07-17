using System.Net.Http.Json;
using System.Text.Json;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Queue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Common.Tests.Hosting;

public class ProcessingEndpointTests
{
    [Fact]
    public async Task StartStopStatusQueueAndHealth_ReturnExpectedContracts()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(Options.Create(new ProcessingServiceOptions
        {
            SyncSchedule = "00:00:01",
            MaxConcurrentProcessingThreads = 3,
            StartProcessingOnStartup = false,
        }));
        builder.Services.AddSingleton<ProcessingLifecycleService>();
        builder.Services.AddSingleton<IProcessingWorkItemStore<TestItem>, TestStore>();
        builder.Services.AddSingleton<IProcessingWorkItemHandler<TestItem>, TestHandler>();

        await using WebApplication app = builder.Build();
        app.MapProcessingEndpoints<TestItem>();
        await app.StartAsync();
        HttpClient client = app.GetTestClient();

        JsonElement initialStatus = await GetJsonAsync(client, "/status");
        Assert.Equal("paused", initialStatus.GetProperty("status").GetString());
        Assert.False(initialStatus.GetProperty("isRunning").GetBoolean());
        Assert.Equal(3, initialStatus.GetProperty("maxConcurrentProcessingThreads").GetInt32());

        JsonElement start = await PostJsonAsync(client, "/processing/start");
        Assert.Equal("running", start.GetProperty("status").GetString());
        Assert.True(start.GetProperty("isRunning").GetBoolean());

        JsonElement aliasStatus = await GetJsonAsync(client, "/api/v1/status");
        Assert.True(aliasStatus.GetProperty("isRunning").GetBoolean());

        JsonElement queue = await GetJsonAsync(client, "/api/v1/processing/queue");
        Assert.Equal(1, queue.GetProperty("processedCount").GetInt32());
        Assert.Equal(2, queue.GetProperty("remainingCount").GetInt32());
        Assert.Equal(3, queue.GetProperty("inFlightCount").GetInt32());
        Assert.Equal(4, queue.GetProperty("errorCount").GetInt32());
        Assert.Equal(12.5, queue.GetProperty("averageItemDurationMs").GetDouble());
        Assert.True(queue.TryGetProperty("lastItemCompletedAt", out JsonElement _));

        JsonElement health = await GetJsonAsync(client, "/api/v1/health");
        Assert.Equal("ok", health.GetProperty("status").GetString());

        JsonElement stop = await PostJsonAsync(client, "/api/v1/processing/stop");
        Assert.Equal("paused", stop.GetProperty("status").GetString());
        Assert.False(stop.GetProperty("isRunning").GetBoolean());
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        JsonDocument document = await JsonDocument.ParseAsync(await client.GetStreamAsync(path));
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.PostAsync(path, null);
        response.EnsureSuccessStatusCode();
        JsonDocument document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }

    private sealed class TestItem;

    private sealed class TestHandler : IProcessingWorkItemHandler<TestItem>
    {
        public Task ProcessAsync(TestItem item, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TestStore : IProcessingWorkItemStore<TestItem>
    {
        public Task<IReadOnlyList<TestItem>> GetPendingAsync(int maxItems, CancellationToken ct) => Task.FromResult<IReadOnlyList<TestItem>>([]);
        public Task<bool> ClaimItemAsync(TestItem item, DateTimeOffset startedAt, CancellationToken ct) => Task.FromResult(false);
        public Task MarkCompleteAsync(TestItem item, DateTimeOffset completedAt, CancellationToken ct) => Task.CompletedTask;
        public Task MarkErrorAsync(TestItem item, string errorMessage, DateTimeOffset completedAt, CancellationToken ct) => Task.CompletedTask;
        public Task<int> ResetOrphanedItemsAsync(TimeSpan olderThan, DateTimeOffset now, CancellationToken ct) => Task.FromResult(0);
        public Task<ProcessingQueueStats> GetQueueStatsAsync(CancellationToken ct)
        {
            ProcessingQueueStats stats = new(1, 2, 3, 4, 12.5, DateTimeOffset.UtcNow);
            return Task.FromResult(stats);
        }
    }
}
