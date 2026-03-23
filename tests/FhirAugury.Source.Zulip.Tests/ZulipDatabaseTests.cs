using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Zulip.Tests;

public class ZulipDatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ZulipDatabase _db;

    public ZulipDatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zulip_test_{Guid.NewGuid()}.db");
        _db = new ZulipDatabase(_dbPath, NullLogger<ZulipDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Initialize_CreatesAllTables()
    {
        using var conn = _db.OpenConnection();
        var tables = GetTableNames(conn);

        Assert.Contains("zulip_streams", tables);
        Assert.Contains("zulip_messages", tables);
        Assert.Contains("sync_state", tables);
        Assert.Contains("index_keywords", tables);
    }

    [Fact]
    public void Initialize_CreatesFtsVirtualTable()
    {
        using var conn = _db.OpenConnection();
        var tables = GetTableNames(conn);

        Assert.Contains("zulip_messages_fts", tables);
    }

    [Fact]
    public void InsertAndSelect_Stream_RoundTrips()
    {
        using var conn = _db.OpenConnection();
        var stream = new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = 42,
            Name = "implementers",
            Description = "Discussion for implementers",
            IsWebPublic = true,
            MessageCount = 500,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };

        ZulipStreamRecord.Insert(conn, stream);
        var result = ZulipStreamRecord.SelectSingle(conn, ZulipStreamId: 42);

        Assert.NotNull(result);
        Assert.Equal("implementers", result.Name);
        Assert.Equal(500, result.MessageCount);
    }

    [Fact]
    public void InsertAndSelect_Message_RoundTrips()
    {
        using var conn = _db.OpenConnection();
        var stream = CreateSampleStream(42, "general");
        ZulipStreamRecord.Insert(conn, stream);

        var msg = CreateSampleMessage(stream.Id, 1001, "general", "R5 ballot", "Alice");
        ZulipMessageRecord.Insert(conn, msg);

        var result = ZulipMessageRecord.SelectSingle(conn, ZulipMessageId: 1001);

        Assert.NotNull(result);
        Assert.Equal("general", result.StreamName);
        Assert.Equal("R5 ballot", result.Topic);
        Assert.Equal("Alice", result.SenderName);
    }

    [Fact]
    public void SelectList_ByStreamName_FiltersCorrectly()
    {
        using var conn = _db.OpenConnection();
        var s1 = CreateSampleStream(1, "general");
        var s2 = CreateSampleStream(2, "committers");
        ZulipStreamRecord.Insert(conn, s1);
        ZulipStreamRecord.Insert(conn, s2);

        ZulipMessageRecord.Insert(conn, CreateSampleMessage(s1.Id, 101, "general", "topic1", "Alice"));
        ZulipMessageRecord.Insert(conn, CreateSampleMessage(s2.Id, 102, "committers", "topic2", "Bob"));
        ZulipMessageRecord.Insert(conn, CreateSampleMessage(s1.Id, 103, "general", "topic3", "Charlie"));

        var general = ZulipMessageRecord.SelectList(conn, StreamName: "general");

        Assert.Equal(2, general.Count);
        Assert.All(general, m => Assert.Equal("general", m.StreamName));
    }

    [Fact]
    public void Fts5_IndexesMessagesOnInsert()
    {
        using var conn = _db.OpenConnection();
        var stream = CreateSampleStream(1, "general");
        ZulipStreamRecord.Insert(conn, stream);

        ZulipMessageRecord.Insert(conn, CreateSampleMessage(stream.Id, 201, "general", "topic", "Alice", "Patient resource is broken"));
        ZulipMessageRecord.Insert(conn, CreateSampleMessage(stream.Id, 202, "general", "topic", "Bob", "Observation code system update"));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ZulipMessageId FROM zulip_messages WHERE Id IN (SELECT rowid FROM zulip_messages_fts WHERE zulip_messages_fts MATCH '\"Patient\"')";
        using var reader = cmd.ExecuteReader();

        var ids = new List<int>();
        while (reader.Read()) ids.Add(reader.GetInt32(0));

        Assert.Single(ids);
        Assert.Equal(201, ids[0]);
    }

    [Fact]
    public void CheckIntegrity_ReturnsOk()
    {
        var result = _db.CheckIntegrity();
        Assert.Equal("ok", result);
    }

    private static ZulipStreamRecord CreateSampleStream(int zulipId, string name) => new()
    {
        Id = ZulipStreamRecord.GetIndex(),
        ZulipStreamId = zulipId,
        Name = name,
        Description = $"Description for {name}",
        IsWebPublic = true,
        MessageCount = 0,
        LastFetchedAt = DateTimeOffset.UtcNow,
    };

    private static ZulipMessageRecord CreateSampleMessage(
        int streamId, int zulipMessageId, string streamName,
        string topic, string senderName, string content = "Test message content") => new()
    {
        Id = ZulipMessageRecord.GetIndex(),
        ZulipMessageId = zulipMessageId,
        StreamId = streamId,
        StreamName = streamName,
        Topic = topic,
        SenderId = zulipMessageId * 10,
        SenderName = senderName,
        SenderEmail = $"{senderName.ToLower()}@example.com",
        ContentHtml = $"<p>{content}</p>",
        ContentPlain = content,
        Timestamp = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        Reactions = null,
    };

    private static List<string> GetTableNames(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'trigger') ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}
