using System.Reflection;
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
/// feature (slot 0517-02, Phase 2): GET /threads/{streamName}/{topic} now
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
        try { File.Delete(_dbPath); } catch { }
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
        object payload = ok.Value!;

        Assert.Equal(42, GetValue<int?>(payload, "streamId"));
        Assert.Equal(3, GetValue<int?>(payload, "messageCount"));
        Assert.Equal("2026-05-01 10:00:00+00:00", GetValue<string?>(payload, "firstMessageAt"));
        Assert.Equal("2026-05-02 09:00:00+00:00", GetValue<string?>(payload, "lastMessageAt"));
        Assert.Equal("first content body that should appear in the excerpt", GetValue<string?>(payload, "firstMessageExcerpt"));
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
        string? excerpt = GetValue<string?>(ok.Value!, "firstMessageExcerpt");

        Assert.NotNull(excerpt);
        Assert.True(excerpt!.Length <= 241, $"excerpt length {excerpt.Length} exceeds 241");
        Assert.EndsWith("…", excerpt);
    }

    [Fact]
    public void GetThread_EmptyTopicReturnsZeroCountAndNullAggregates()
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.GetThread("unknown", "topic", limit: null));
        object payload = ok.Value!;

        Assert.Null(GetValue<int?>(payload, "streamId"));
        Assert.Equal(0, GetValue<int?>(payload, "messageCount"));
        Assert.Null(GetValue<string?>(payload, "firstMessageAt"));
        Assert.Null(GetValue<string?>(payload, "lastMessageAt"));
        Assert.Null(GetValue<string?>(payload, "firstMessageExcerpt"));
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

    private static T? GetValue<T>(object source, string propertyName)
    {
        PropertyInfo prop = source.GetType().GetProperty(propertyName)
            ?? throw new InvalidOperationException($"Property '{propertyName}' not found on {source.GetType().Name}");
        object? value = prop.GetValue(source);
        return (T?)value;
    }
}
