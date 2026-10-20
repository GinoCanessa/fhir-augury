using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Source.Zulip.Api;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Controllers;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Tests;

/// <summary>
/// Pins the response-shape additions introduced by the preparer-hydration
/// feature (slot 0517-02, Phase 2): GET /threads?streamName=...&amp;topic=... now
/// returns streamId, messageCount, firstMessageAt, lastMessageAt, and
/// firstMessageExcerpt alongside the existing fields.
/// </summary>
public class ThreadsControllerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ZulipDatabase _db;
    private readonly ThreadsController _controller;

    public ThreadsControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zulip_threads_ctrl_{Guid.NewGuid():N}.db");
        _db = new ZulipDatabase(_dbPath, NullLogger<ZulipDatabase>.Instance);
        _db.Initialize();
        IOptions<ZulipServiceOptions> options = Options.Create(new ZulipServiceOptions { BaseUrl = "https://chat.example.com" });
        _controller = new ThreadsController(_db, options);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    [Fact]
    public void GetThread_PopulatesAggregateFieldsAndStreamId()
    {
        ZulipStreamRecord stream = new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = 42,
            Name = "implementers",
            Description = "test",
            IsWebPublic = true,
            MessageCount = 0,
            IncludeStream = true,
            BaselineValue = 5,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
        using (SqliteConnection conn = _db.OpenConnection())
        {
            ZulipStreamRecord.Insert(conn, stream);
            ZulipMessageRecord.Insert(conn, CreateMessage(stream.Id, 1001, "implementers", "ballot", "Alice",
                "first content body that should appear in the excerpt", new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero)));
            ZulipMessageRecord.Insert(conn, CreateMessage(stream.Id, 1002, "implementers", "ballot", "Bob",
                "follow-up", new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero)));
            ZulipMessageRecord.Insert(conn, CreateMessage(stream.Id, 1003, "implementers", "ballot", "Carol",
                "last word", new DateTimeOffset(2026, 5, 2, 9, 0, 0, TimeSpan.Zero)));
        }

        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.GetThread("implementers", "ballot", limit: null));
        ZulipThreadResponse payload = Assert.IsType<ZulipThreadResponse>(ok.Value);

        Assert.Equal(42, payload.StreamId);
        Assert.Equal(3, payload.MessageCount);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero), payload.FirstMessageAt);
        Assert.Equal(new DateTimeOffset(2026, 5, 2, 9, 0, 0, TimeSpan.Zero), payload.LastMessageAt);
        Assert.Equal("first content body that should appear in the excerpt", payload.FirstMessageExcerpt);
        Assert.Empty(payload.Diagnostics);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("2026-05-01T10:00:00+00:00", json.RootElement.GetProperty("firstMessageAt").GetString());
        Assert.Equal("2026-05-02T09:00:00+00:00", json.RootElement.GetProperty("lastMessageAt").GetString());
        Assert.All(payload.Messages, message => Assert.Equal(TimeSpan.Zero, message.Timestamp!.Value.Offset));
    }

    [Fact]
    public void GetThread_TruncatesLongExcerptToWordBoundary()
    {
        string content = string.Join(' ', Enumerable.Repeat("word", 100));
        ZulipStreamRecord stream = new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = 99,
            Name = "general",
            Description = null,
            IsWebPublic = true,
            MessageCount = 0,
            IncludeStream = true,
            BaselineValue = 5,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
        using (SqliteConnection conn = _db.OpenConnection())
        {
            ZulipStreamRecord.Insert(conn, stream);
            ZulipMessageRecord.Insert(conn, CreateMessage(stream.Id, 1, "general", "long", "Alice", content, DateTimeOffset.UtcNow));
        }

        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.GetThread("general", "long", limit: null));
        string? excerpt = Assert.IsType<ZulipThreadResponse>(ok.Value).FirstMessageExcerpt;

        Assert.NotNull(excerpt);
        Assert.True(excerpt!.Length <= 241, $"excerpt length {excerpt.Length} exceeds 241");
        Assert.EndsWith("…", excerpt);
    }

    [Fact]
    public void GetThread_EmptyTopicReturnsZeroCountAndNullAggregates()
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.GetThread("unknown", "topic", limit: null));
        ZulipThreadResponse payload = Assert.IsType<ZulipThreadResponse>(ok.Value);

        Assert.Null(payload.StreamId);
        Assert.Equal(0, payload.MessageCount);
        Assert.Null(payload.FirstMessageAt);
        Assert.Null(payload.LastMessageAt);
        Assert.Null(payload.FirstMessageExcerpt);
        Assert.Equal(0, payload.Total);
        Assert.Empty(payload.Messages);
        Assert.Empty(payload.Diagnostics);
        Assert.Equal("https://chat.example.com/#narrow/stream/unknown/topic/topic", payload.Url);
    }

    [Fact]
    public void GetThread_SlashStreamName_ReturnsRows()
    {
        ZulipStreamRecord stream = new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = 4242,
            Name = "fhir/infrastructure-wg",
            Description = "infra",
            IsWebPublic = true,
            MessageCount = 0,
            IncludeStream = true,
            BaselineValue = 5,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };
        using (SqliteConnection conn = _db.OpenConnection())
        {
            ZulipStreamRecord.Insert(conn, stream);
            ZulipMessageRecord.Insert(conn, CreateMessage(stream.Id, 2001, "fhir/infrastructure-wg", "ballot", "Alice",
                "content body", new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero)));
        }

        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.GetThread("fhir/infrastructure-wg", "ballot", limit: null));
        ZulipThreadResponse payload = Assert.IsType<ZulipThreadResponse>(ok.Value);
        Assert.Equal("fhir/infrastructure-wg", payload.Stream);
        Assert.Equal(4242, payload.StreamId);
        Assert.Equal(1, payload.Total);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(-1, 3)]
    [InlineData(10000, 3)]
    public void GetThread_LimitPreservesTotalsContentAndExcerptSemantics(int limit, int expectedTotal)
    {
        using (SqliteConnection connection = _db.OpenConnection())
        {
            for (int index = 1; index <= 3; index++)
            {
                ZulipMessageRecord.Insert(connection, CreateMessage(1, 100 + index, "implementers", "topic", "Alice",
                    $"content {index}", new DateTimeOffset(2026, 5, index, 10, 0, 0, TimeSpan.Zero)));
            }
        }

        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.GetThread("implementers", "topic", limit));
        ZulipThreadResponse payload = Assert.IsType<ZulipThreadResponse>(ok.Value);

        Assert.Equal(expectedTotal, payload.Total);
        Assert.Equal(expectedTotal, payload.Messages.Count);
        Assert.Equal(3, payload.MessageCount);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero), payload.FirstMessageAt);
        Assert.Equal(new DateTimeOffset(2026, 5, 3, 10, 0, 0, TimeSpan.Zero), payload.LastMessageAt);
        Assert.Equal(limit == 0 ? null : "content 1", payload.FirstMessageExcerpt);
        for (int index = 0; index < expectedTotal; index++)
        {
            Assert.Equal($"content {index + 1}", payload.Messages[index].Content);
            Assert.Equal($"<p>content {index + 1}</p>", payload.Messages[index].ContentHtml);
            Assert.Equal("Alice", payload.Messages[index].Sender);
        }
    }

    [Fact]
    public void GetThread_MixedOffsetAggregatesUseInstantsWithoutChangingContentOrder()
    {
        using (SqliteConnection connection = _db.OpenConnection())
        {
            ZulipMessageRecord.Insert(connection, CreateMessage(1, 101, "implementers", "topic", "Alice", "lexically first",
                DateTimeOffset.UnixEpoch));
            ZulipMessageRecord.Insert(connection, CreateMessage(1, 102, "implementers", "topic", "Bob", "earliest instant",
                DateTimeOffset.UnixEpoch));
            using SqliteCommand update = new("""
                UPDATE zulip_messages SET Timestamp = '2026-04-30 23:00:00-02:00' WHERE ZulipMessageId = 101;
                UPDATE zulip_messages SET Timestamp = '2026-05-01T00:30:00+02:00' WHERE ZulipMessageId = 102;
                """, connection);
            update.ExecuteNonQuery();
        }

        ZulipThreadResponse payload = Assert.IsType<ZulipThreadResponse>(
            Assert.IsType<OkObjectResult>(_controller.GetThread("implementers", "topic", 1)).Value);

        Assert.Equal(1, payload.Total);
        Assert.Equal(2, payload.MessageCount);
        Assert.Equal("lexically first", payload.FirstMessageExcerpt);
        Assert.Equal(101, Assert.Single(payload.Messages).Id);
        Assert.Equal(new DateTimeOffset(2026, 4, 30, 22, 30, 0, TimeSpan.Zero), payload.FirstMessageAt);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 1, 0, 0, TimeSpan.Zero), payload.LastMessageAt);
    }

    [Fact]
    public void GetThread_InvalidTimestampIsNullWithDiagnosticsNotRawText()
    {
        using (SqliteConnection connection = _db.OpenConnection())
        {
            ZulipMessageRecord.Insert(connection, CreateMessage(1, 101, "implementers", "topic", "Alice", "content unchanged",
                DateTimeOffset.UnixEpoch));
            using SqliteCommand update = new("UPDATE zulip_messages SET Timestamp = 'invalid time'", connection);
            update.ExecuteNonQuery();
        }

        ZulipThreadResponse payload = Assert.IsType<ZulipThreadResponse>(
            Assert.IsType<OkObjectResult>(_controller.GetThread("implementers", "topic", null)).Value);

        Assert.Equal(1, payload.MessageCount);
        Assert.Equal("content unchanged", payload.FirstMessageExcerpt);
        Assert.Null(payload.FirstMessageAt);
        Assert.Null(payload.LastMessageAt);
        Assert.Null(Assert.Single(payload.Messages).Timestamp);
        Assert.Equal([ZulipReferenceDiagnosticCode.InvalidTimestamp], payload.Diagnostics);
        Assert.DoesNotContain("invalid time", JsonSerializer.Serialize(payload));
    }

    [Fact]
    public void GetThread_MissingStreamNameOrTopic_ReturnsBadRequest()
    {
        Assert.IsType<BadRequestObjectResult>(_controller.GetThread(null, "ballot", limit: null));
        Assert.IsType<BadRequestObjectResult>(_controller.GetThread("   ", "ballot", limit: null));
        Assert.IsType<BadRequestObjectResult>(_controller.GetThread("implementers", null, limit: null));
        Assert.IsType<BadRequestObjectResult>(_controller.GetThread("implementers", "   ", limit: null));
    }

    [Fact]
    public void GetThreadSnapshot_MissingStreamNameOrTopic_ReturnsBadRequest()
    {
        Assert.IsType<BadRequestObjectResult>(_controller.GetThreadSnapshot(null, "ballot"));
        Assert.IsType<BadRequestObjectResult>(_controller.GetThreadSnapshot("implementers", null));
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

}
