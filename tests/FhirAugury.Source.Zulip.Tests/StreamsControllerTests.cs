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
/// Direct-controller guards for the query-string topics route (slot 0707-05):
/// a slash-named stream resolves to rows through the parameterized SQL, and a
/// missing <c>streamName</c> returns <see cref="BadRequestObjectResult"/> instead
/// of a silent empty 200.
/// </summary>
public class StreamsControllerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ZulipDatabase _db;
    private readonly StreamsController _controller;

    public StreamsControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zulip_streams_ctrl_{Guid.NewGuid():N}.db");
        _db = new ZulipDatabase(_dbPath, NullLogger<ZulipDatabase>.Instance);
        _db.Initialize();
        IOptions<ZulipServiceOptions> options = Options.Create(new ZulipServiceOptions { BaseUrl = "https://chat.example.com" });
        _controller = new StreamsController(_db, options);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    [Fact]
    public void GetStreamTopics_SlashStreamName_ReturnsRows()
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
            ZulipMessageRecord.Insert(conn, CreateMessage(stream.Id, 3001, "fhir/infrastructure-wg", "ballot", "Alice",
                "content body", new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero)));
            ZulipMessageRecord.Insert(conn, CreateMessage(stream.Id, 3002, "fhir/infrastructure-wg", "governance", "Bob",
                "second body", new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.Zero)));
        }

        OkObjectResult ok = Assert.IsType<OkObjectResult>(_controller.GetStreamTopics("fhir/infrastructure-wg", limit: null, offset: null));
        Assert.Equal("fhir/infrastructure-wg", GetValue<string?>(ok.Value!, "stream"));
        Assert.Equal(2, GetValue<int?>(ok.Value!, "total"));
    }

    [Fact]
    public void GetStreamTopics_MissingStreamName_ReturnsBadRequest()
    {
        Assert.IsType<BadRequestObjectResult>(_controller.GetStreamTopics(null, limit: null, offset: null));
        Assert.IsType<BadRequestObjectResult>(_controller.GetStreamTopics("   ", limit: null, offset: null));
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
