using System.Net;
using System.Text.Json;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Controllers;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Tests;

/// <summary>
/// The decisive route → SQL regression for slot 0707-05: hosts the Zulip
/// controllers through real ASP.NET Core routing (UseTestServer) and proves that
/// a stream name containing reserved characters (a literal <c>/</c>) sent as a
/// percent-encoded query value round-trips to the parameterized SQL exact-match.
/// Under the old path-parameter routes the encoded slash reached SQL un-decoded
/// and matched zero rows; the query-string contract fixes that for the whole
/// reserved-character class on both stream names and topics.
/// </summary>
public class ZulipRoutingIntegrationTests : IAsyncLifetime
{
    private const string SlashStream = "fhir/infrastructure-wg";
    private const string ReservedTopic = "Message forbids entry.request & entry.response";

    private string _dbPath = string.Empty;
    private ZulipDatabase _db = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zulip_routing_{Guid.NewGuid():N}.db");
        _db = new ZulipDatabase(_dbPath, NullLogger<ZulipDatabase>.Instance);
        _db.Initialize();
        Seed(_db);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(_db);
        builder.Services.AddSingleton(Options.Create(new ZulipServiceOptions { BaseUrl = "https://chat.example.com" }));
        builder.Services.AddControllers().AddApplicationPart(typeof(StreamsController).Assembly);

        _app = builder.Build();
        _app.MapControllers();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    [Fact]
    public async Task StreamTopics_EncodedSlashStream_ResolvesThroughRoutingToSql()
    {
        string url = $"/api/v1/streams/topics?streamName={Uri.EscapeDataString(SlashStream)}&limit=5";
        using HttpResponseMessage response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement root = await ReadJsonAsync(response);
        Assert.Equal(SlashStream, root.GetProperty("stream").GetString());
        JsonElement topics = root.GetProperty("topics");
        Assert.Equal(JsonValueKind.Array, topics.ValueKind);
        Assert.True(topics.GetArrayLength() > 0, "expected at least one topic for the slash-named stream");
    }

    [Fact]
    public async Task Thread_EncodedSlashStreamAndReservedTopic_ReturnsMessages()
    {
        string url = $"/api/v1/threads?streamName={Uri.EscapeDataString(SlashStream)}&topic={Uri.EscapeDataString(ReservedTopic)}";
        using HttpResponseMessage response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement root = await ReadJsonAsync(response);
        Assert.Equal(SlashStream, root.GetProperty("stream").GetString());
        Assert.Equal(ReservedTopic, root.GetProperty("topic").GetString());
        Assert.True(root.GetProperty("messages").GetArrayLength() > 0, "expected messages for the slash-named stream + reserved topic");
    }

    [Fact]
    public async Task ThreadSnapshot_EncodedSlashStreamAndReservedTopic_Returns200()
    {
        string url = $"/api/v1/threads/snapshot?streamName={Uri.EscapeDataString(SlashStream)}&topic={Uri.EscapeDataString(ReservedTopic)}";
        using HttpResponseMessage response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StreamTopics_MissingStreamName_Returns400()
    {
        using HttpResponseMessage response = await _client.GetAsync("/api/v1/streams/topics");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Thread_MissingTopic_Returns400()
    {
        string url = $"/api/v1/threads?streamName={Uri.EscapeDataString(SlashStream)}";
        using HttpResponseMessage response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static void Seed(ZulipDatabase db)
    {
        ZulipStreamRecord stream = new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = 4242,
            Name = SlashStream,
            Description = "infrastructure",
            IsWebPublic = true,
            MessageCount = 0,
            IncludeStream = true,
            BaselineValue = 5,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };

        using SqliteConnection conn = db.OpenConnection();
        ZulipStreamRecord.Insert(conn, stream);
        ZulipMessageRecord.Insert(conn, CreateMessage(stream.Id, 5001, SlashStream, ReservedTopic, "Alice",
            "first body of the reserved-character topic", new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero)));
        ZulipMessageRecord.Insert(conn, CreateMessage(stream.Id, 5002, SlashStream, ReservedTopic, "Bob",
            "second body", new DateTimeOffset(2026, 6, 1, 11, 0, 0, TimeSpan.Zero)));
    }

    private static ZulipMessageRecord CreateMessage(int streamId, int zulipMessageId, string streamName, string topic, string sender, string content, DateTimeOffset timestamp) => new()
    {
        Id = ZulipMessageRecord.GetIndex(),
        ZulipMessageId = zulipMessageId,
        StreamId = streamId,
        StreamName = streamName,
        Topic = topic,
        SenderId = zulipMessageId * 10,
        SenderName = sender,
        SenderEmail = $"{sender.ToLower()}@example.com",
        ContentHtml = $"<p>{content}</p>",
        ContentPlain = content,
        Timestamp = timestamp,
        CreatedAt = timestamp,
        Reactions = null,
    };

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        JsonDocument document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }
}
