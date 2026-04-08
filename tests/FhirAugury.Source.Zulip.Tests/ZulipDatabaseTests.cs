using FhirAugury.Common.Api;
using FhirAugury.Common.Database;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using Microsoft.Data.Sqlite;
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
        using SqliteConnection conn = _db.OpenConnection();
        List<string> tables = GetTableNames(conn);

        Assert.Contains("zulip_streams", tables);
        Assert.Contains("zulip_messages", tables);
        Assert.Contains("sync_state", tables);
        Assert.Contains("index_keywords", tables);
    }

    [Fact]
    public void Initialize_CreatesFtsVirtualTable()
    {
        using SqliteConnection conn = _db.OpenConnection();
        List<string> tables = GetTableNames(conn);

        Assert.Contains("zulip_messages_fts", tables);
    }

    [Fact]
    public void InsertAndSelect_Stream_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        ZulipStreamRecord stream = new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = 42,
            Name = "implementers",
            Description = "Discussion for implementers",
            IsWebPublic = true,
            MessageCount = 500,
            IncludeStream = true,
            BaselineValue = 5,
            LastFetchedAt = DateTimeOffset.UtcNow,
        };

        ZulipStreamRecord.Insert(conn, stream);
        ZulipStreamRecord? result = ZulipStreamRecord.SelectSingle(conn, ZulipStreamId: 42);

        Assert.NotNull(result);
        Assert.Equal("implementers", result.Name);
        Assert.Equal(500, result.MessageCount);
    }

    [Fact]
    public void InsertAndSelect_Message_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        ZulipStreamRecord stream = CreateSampleStream(42, "general");
        ZulipStreamRecord.Insert(conn, stream);

        ZulipMessageRecord msg = CreateSampleMessage(stream.Id, 1001, "general", "R5 ballot", "Alice");
        ZulipMessageRecord.Insert(conn, msg);

        ZulipMessageRecord? result = ZulipMessageRecord.SelectSingle(conn, ZulipMessageId: 1001);

        Assert.NotNull(result);
        Assert.Equal("general", result.StreamName);
        Assert.Equal("R5 ballot", result.Topic);
        Assert.Equal("Alice", result.SenderName);
    }

    [Fact]
    public void SelectList_ByStreamName_FiltersCorrectly()
    {
        using SqliteConnection conn = _db.OpenConnection();
        ZulipStreamRecord s1 = CreateSampleStream(1, "general");
        ZulipStreamRecord s2 = CreateSampleStream(2, "committers");
        ZulipStreamRecord.Insert(conn, s1);
        ZulipStreamRecord.Insert(conn, s2);

        ZulipMessageRecord.Insert(conn, CreateSampleMessage(s1.Id, 101, "general", "topic1", "Alice"));
        ZulipMessageRecord.Insert(conn, CreateSampleMessage(s2.Id, 102, "committers", "topic2", "Bob"));
        ZulipMessageRecord.Insert(conn, CreateSampleMessage(s1.Id, 103, "general", "topic3", "Charlie"));

        List<ZulipMessageRecord> general = ZulipMessageRecord.SelectList(conn, StreamName: "general");

        Assert.Equal(2, general.Count);
        Assert.All(general, m => Assert.Equal("general", m.StreamName));
    }

    [Fact]
    public void Fts5_IndexesMessagesOnInsert()
    {
        using SqliteConnection conn = _db.OpenConnection();
        ZulipStreamRecord stream = CreateSampleStream(1, "general");
        ZulipStreamRecord.Insert(conn, stream);

        ZulipMessageRecord.Insert(conn, CreateSampleMessage(stream.Id, 201, "general", "topic", "Alice", "Patient resource is broken"));
        ZulipMessageRecord.Insert(conn, CreateSampleMessage(stream.Id, 202, "general", "topic", "Bob", "Observation code system update"));

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ZulipMessageId FROM zulip_messages WHERE Id IN (SELECT rowid FROM zulip_messages_fts WHERE zulip_messages_fts MATCH '\"Patient\"')";
        using SqliteDataReader reader = cmd.ExecuteReader();

        List<int> ids = new List<int>();
        while (reader.Read()) ids.Add(reader.GetInt32(0));

        Assert.Single(ids);
        Assert.Equal(201, ids[0]);
    }

    [Fact]
    public void CheckIntegrity_ReturnsOk()
    {
        string result = _db.CheckIntegrity();
        Assert.Equal("ok", result);
    }

    [Fact]
    public void InsertAndSelect_Stream_BaselineValueDefaultsTo5()
    {
        using SqliteConnection conn = _db.OpenConnection();
        ZulipStreamRecord stream = CreateSampleStream(99, "general");
        ZulipStreamRecord.Insert(conn, stream);

        ZulipStreamRecord? result = ZulipStreamRecord.SelectSingle(conn, ZulipStreamId: 99);
        Assert.NotNull(result);
        Assert.Equal(5, result.BaselineValue);
    }

    [Fact]
    public void InsertAndSelect_Stream_BaselineValueRoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        ZulipStreamRecord stream = CreateSampleStream(100, "notifications");
        stream.BaselineValue = 1;
        ZulipStreamRecord.Insert(conn, stream);

        ZulipStreamRecord? result = ZulipStreamRecord.SelectSingle(conn, ZulipStreamId: 100);
        Assert.NotNull(result);
        Assert.Equal(1, result.BaselineValue);
    }

    [Fact]
    public void BaselineValue_AffectsFtsSearchOrdering()
    {
        using SqliteConnection conn = _db.OpenConnection();

        // Create two streams: one high-value discussion, one low-value notifications
        ZulipStreamRecord discussion = CreateSampleStream(10, "implementers");
        discussion.BaselineValue = 8;
        ZulipStreamRecord.Insert(conn, discussion);

        ZulipStreamRecord notifications = CreateSampleStream(11, "committers/notification");
        notifications.BaselineValue = 1;
        ZulipStreamRecord.Insert(conn, notifications);

        // Insert identical content in both streams
        ZulipMessageRecord.Insert(conn, CreateSampleMessage(discussion.Id, 301, "implementers", "FHIR ballot", "Alice", "Patient resource discussion"));
        ZulipMessageRecord.Insert(conn, CreateSampleMessage(notifications.Id, 302, "committers/notification", "FHIR build", "Bot", "Patient resource discussion"));

        // Search with baseline-weighted ordering
        string sql = """
            SELECT zm.StreamName,
                   zulip_messages_fts.rank,
                   COALESCE(zs.BaselineValue, 5) as BaselineValue
            FROM zulip_messages_fts
            JOIN zulip_messages zm ON zm.Id = zulip_messages_fts.rowid
            LEFT JOIN zulip_streams zs ON zs.Id = zm.StreamId
            WHERE zulip_messages_fts MATCH '"Patient"'
            ORDER BY (zulip_messages_fts.rank * COALESCE(zs.BaselineValue, 5) / 5.0)
            """;

        using SqliteCommand cmd = new SqliteCommand(sql, conn);
        using SqliteDataReader reader = cmd.ExecuteReader();

        List<(string StreamName, int BaselineValue)> results = [];
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.GetInt32(2)));
        }

        Assert.Equal(2, results.Count);
        // High-baseline stream should rank first (more negative weighted rank)
        Assert.Equal("implementers", results[0].StreamName);
        Assert.Equal("committers/notification", results[1].StreamName);
    }

    [Fact]
    public void MigrateSchema_AddsBaselineValueToExistingDatabase()
    {
        // Create a second database to simulate migration on an existing DB
        string dbPath2 = Path.Combine(Path.GetTempPath(), $"zulip_migrate_{Guid.NewGuid()}.db");
        try
        {
            // Create a bare DB with the old schema (no BaselineValue column)
            using (SqliteConnection rawConn = new SqliteConnection($"Data Source={dbPath2}"))
            {
                rawConn.Open();
                using SqliteCommand cmd = rawConn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS zulip_streams (
                        Id INTEGER PRIMARY KEY,
                        ZulipStreamId INTEGER UNIQUE,
                        Name TEXT,
                        Description TEXT,
                        IsWebPublic INTEGER,
                        MessageCount INTEGER,
                        IncludeStream INTEGER,
                        LastFetchedAt TEXT
                    );
                    INSERT INTO zulip_streams (Id, ZulipStreamId, Name, Description, IsWebPublic, MessageCount, IncludeStream, LastFetchedAt)
                    VALUES (1, 42, 'old-stream', 'pre-migration', 1, 100, 1, '2025-01-01');
                    """;
                cmd.ExecuteNonQuery();
            }

            // Open with ZulipDatabase which triggers migration
            using ZulipDatabase migratedDb = new ZulipDatabase(dbPath2, Microsoft.Extensions.Logging.Abstractions.NullLogger<ZulipDatabase>.Instance);
            migratedDb.Initialize();

            using SqliteConnection conn = migratedDb.OpenConnection();
            ZulipStreamRecord? stream = ZulipStreamRecord.SelectSingle(conn, ZulipStreamId: 42);

            Assert.NotNull(stream);
            Assert.Equal("old-stream", stream.Name);
            // Migration should have added BaselineValue with default 5
            Assert.Equal(5, stream.BaselineValue);
        }
        finally
        {
            try { File.Delete(dbPath2); } catch { }
        }
    }

    private static ZulipStreamRecord CreateSampleStream(int zulipId, string name) => new()
    {
        Id = ZulipStreamRecord.GetIndex(),
        ZulipStreamId = zulipId,
        Name = name,
        Description = $"Description for {name}",
        IsWebPublic = true,
        MessageCount = 0,
        IncludeStream = true,
        BaselineValue = 5,
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

    // ── Keyword query tests ────────────────────────────────────────────

    [Fact]
    public void GetKeywordsForItem_ReturnsMatchingKeywords()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('message', 'test-item-1', 'patient', 5, 'word', 4.5),
                   ('message', 'test-item-1', 'Patient.name', 3, 'fhir_path', 3.2),
                   ('message', 'test-item-1', '$validate', 1, 'fhir_operation', 2.1),
                   ('message', 'test-item-2', 'observation', 2, 'word', 1.5);
            """;
        insertCmd.ExecuteNonQuery();

        List<KeywordEntry> results = SourceDatabase.GetKeywordsForItem(connection, "test-item-1");
        Assert.Equal(3, results.Count);
        Assert.Equal("patient", results[0].Keyword);
        Assert.Equal(4.5, results[0].Bm25Score);
        Assert.Equal("Patient.name", results[1].Keyword);
        Assert.Equal("$validate", results[2].Keyword);
    }

    [Fact]
    public void GetKeywordsForItem_FiltersByKeywordType()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('message', 'test-filter-1', 'patient', 5, 'word', 4.5),
                   ('message', 'test-filter-1', 'Patient.name', 3, 'fhir_path', 3.2),
                   ('message', 'test-filter-1', '$validate', 1, 'fhir_operation', 2.1);
            """;
        insertCmd.ExecuteNonQuery();

        List<KeywordEntry> results = SourceDatabase.GetKeywordsForItem(connection, "test-filter-1", keywordType: "fhir_path");
        Assert.Single(results);
        Assert.Equal("Patient.name", results[0].Keyword);
    }

    [Fact]
    public void GetKeywordsForItem_RespectsLimit()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('message', 'test-limit-1', 'a', 1, 'word', 3.0),
                   ('message', 'test-limit-1', 'b', 1, 'word', 2.0),
                   ('message', 'test-limit-1', 'c', 1, 'word', 1.0);
            """;
        insertCmd.ExecuteNonQuery();

        List<KeywordEntry> results = SourceDatabase.GetKeywordsForItem(connection, "test-limit-1", limit: 2);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void GetKeywordsForItem_ReturnsEmpty_WhenNoMatch()
    {
        using SqliteConnection connection = _db.OpenConnection();
        List<KeywordEntry> results = SourceDatabase.GetKeywordsForItem(connection, "nonexistent-item");
        Assert.Empty(results);
    }

    [Fact]
    public void GetContentTypeForItem_ReturnsContentType()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('message', 'test-ct-1', 'patient', 1, 'word', 1.0);
            """;
        insertCmd.ExecuteNonQuery();

        string contentType = SourceDatabase.GetContentTypeForItem(connection, "test-ct-1");
        Assert.Equal("message", contentType);
    }

    [Fact]
    public void GetRelatedByKeyword_FindsRelatedItems()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('message', 'seed-1', 'patient', 5, 'word', 4.0),
                   ('message', 'seed-1', 'observation', 3, 'word', 3.0),
                   ('message', 'related-1', 'patient', 4, 'word', 3.5),
                   ('message', 'related-1', 'observation', 2, 'word', 2.0),
                   ('message', 'related-2', 'patient', 1, 'word', 1.0),
                   ('message', 'unrelated', 'medication', 5, 'word', 5.0);
            """;
        insertCmd.ExecuteNonQuery();

        var results = SourceDatabase.GetRelatedByKeyword(connection, "seed-1", minScore: 0.0);
        Assert.True(results.Count >= 1);
        Assert.Equal("related-1", results[0].SourceId);
        Assert.Contains("patient", results[0].SharedKeywords);
    }

    [Fact]
    public void GetRelatedByKeyword_ExcludesSeedItem()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('message', 'self-1', 'patient', 5, 'word', 4.0),
                   ('message', 'other-1', 'patient', 3, 'word', 3.0);
            """;
        insertCmd.ExecuteNonQuery();

        var results = SourceDatabase.GetRelatedByKeyword(connection, "self-1", minScore: 0.0);
        Assert.DoesNotContain(results, r => r.SourceId == "self-1");
    }

    [Fact]
    public void GetRelatedByKeyword_RespectsMinScore()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('message', 'high-seed', 'patient', 5, 'word', 4.0),
                   ('message', 'high-match', 'patient', 4, 'word', 3.5),
                   ('message', 'low-match', 'patient', 1, 'word', 0.001);
            """;
        insertCmd.ExecuteNonQuery();

        var results = SourceDatabase.GetRelatedByKeyword(connection, "high-seed", minScore: 1.0);
        Assert.DoesNotContain(results, r => r.SourceId == "low-match");
    }

    private static List<string> GetTableNames(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'trigger') ORDER BY name";
        using SqliteDataReader reader = cmd.ExecuteReader();
        List<string> names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}
