using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using FhirAugury.Source.Zulip.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Zulip.Tests;

public class ZulipTicketIndexerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ZulipDatabase _db;
    private readonly ZulipTicketIndexer _indexer;

    public ZulipTicketIndexerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zulip_ticket_test_{Guid.NewGuid()}.db");
        _db = new ZulipDatabase(_dbPath, NullLogger<ZulipDatabase>.Instance);
        _db.Initialize();
        _indexer = new ZulipTicketIndexer(_db, NullLogger<ZulipTicketIndexer>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    // ── Full Rebuild ─────────────────────────────────────────────────

    [Fact]
    public void RebuildFullIndex_IndexesMessageTickets()
    {
        InsertStream(1, "general");
        InsertMessage(1, 1001, "general", "ballot", "See FHIR-43499 for the resolution");

        _indexer.RebuildFullIndex();

        using SqliteConnection conn = _db.OpenConnection();
        List<ZulipMessageTicketRecord> tickets = ZulipMessageTicketRecord.SelectList(conn);

        Assert.Single(tickets);
        Assert.Equal(1001, tickets[0].ZulipMessageId);
        Assert.Equal("FHIR-43499", tickets[0].JiraKey);
    }

    [Fact]
    public void RebuildFullIndex_AggregatesThreadTickets()
    {
        InsertStream(1, "general");
        InsertMessage(1, 1001, "general", "ballot", "See FHIR-100 for details");
        InsertMessage(1, 1002, "general", "ballot", "Also FHIR-100 is relevant");
        InsertMessage(1, 1003, "general", "ballot", "And FHIR-100 again");

        _indexer.RebuildFullIndex();

        using SqliteConnection conn = _db.OpenConnection();
        List<ZulipThreadTicketRecord> threads = ZulipThreadTicketRecord.SelectList(conn);

        Assert.Single(threads);
        Assert.Equal("general", threads[0].StreamName);
        Assert.Equal("ballot", threads[0].Topic);
        Assert.Equal("FHIR-100", threads[0].JiraKey);
        Assert.Equal(3, threads[0].ReferenceCount);
    }

    [Fact]
    public void RebuildFullIndex_ClearsExistingData()
    {
        InsertStream(1, "general");
        InsertMessage(1, 1001, "general", "ballot", "FHIR-100 mentioned");

        _indexer.RebuildFullIndex();
        _indexer.RebuildFullIndex();

        using SqliteConnection conn = _db.OpenConnection();
        List<ZulipMessageTicketRecord> tickets = ZulipMessageTicketRecord.SelectList(conn);

        Assert.Single(tickets);
    }

    [Fact]
    public void RebuildFullIndex_MultiplePatternsInOneMessage()
    {
        InsertStream(1, "general");
        InsertMessage(1, 1001, "general", "ballot", "FHIR-100, J#200, and GF#300 all relevant");

        _indexer.RebuildFullIndex();

        using SqliteConnection conn = _db.OpenConnection();
        List<ZulipMessageTicketRecord> tickets = ZulipMessageTicketRecord.SelectList(conn, ZulipMessageId: 1001);

        Assert.Equal(3, tickets.Count);
        Assert.Contains(tickets, t => t.JiraKey == "FHIR-100");
        Assert.Contains(tickets, t => t.JiraKey == "FHIR-200");
        Assert.Contains(tickets, t => t.JiraKey == "GF-300");
    }

    // ── Thread Aggregation ───────────────────────────────────────────

    [Fact]
    public void ThreadTicket_ReferenceCount_Correct()
    {
        InsertStream(1, "general");
        InsertMessage(1, 1001, "general", "topic", "FHIR-100 first", ts: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        InsertMessage(1, 1002, "general", "topic", "FHIR-100 second", ts: new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero));
        InsertMessage(1, 1003, "general", "topic", "FHIR-100 third", ts: new DateTimeOffset(2024, 1, 3, 0, 0, 0, TimeSpan.Zero));

        _indexer.RebuildFullIndex();

        using SqliteConnection conn = _db.OpenConnection();
        List<ZulipThreadTicketRecord> threads = ZulipThreadTicketRecord.SelectList(conn, JiraKey: "FHIR-100");

        Assert.Single(threads);
        Assert.Equal(3, threads[0].ReferenceCount);
    }

    [Fact]
    public void ThreadTicket_FirstSeenAt_LastSeenAt_Correct()
    {
        InsertStream(1, "general");
        DateTimeOffset earliest = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset latest = new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero);
        InsertMessage(1, 1001, "general", "topic", "FHIR-100 first", ts: earliest);
        InsertMessage(1, 1002, "general", "topic", "FHIR-100 latest", ts: latest);

        _indexer.RebuildFullIndex();

        using SqliteConnection conn = _db.OpenConnection();
        List<ZulipThreadTicketRecord> threads = ZulipThreadTicketRecord.SelectList(conn, JiraKey: "FHIR-100");

        Assert.Single(threads);
        Assert.Equal(earliest, threads[0].FirstSeenAt);
        Assert.Equal(latest, threads[0].LastSeenAt);
    }

    [Fact]
    public void ThreadTicket_MultipleTicketsPerThread()
    {
        InsertStream(1, "general");
        InsertMessage(1, 1001, "general", "topic", "FHIR-100 mentioned");
        InsertMessage(1, 1002, "general", "topic", "FHIR-200 different ticket");

        _indexer.RebuildFullIndex();

        using SqliteConnection conn = _db.OpenConnection();
        List<ZulipThreadTicketRecord> threads = ZulipThreadTicketRecord.SelectList(conn, StreamName: "general", Topic: "topic");

        Assert.Equal(2, threads.Count);
        Assert.Contains(threads, t => t.JiraKey == "FHIR-100");
        Assert.Contains(threads, t => t.JiraKey == "FHIR-200");
    }

    // ── HTML Scanning ────────────────────────────────────────────────

    [Fact]
    public void ExtractsFromHtml_JiraUrls()
    {
        InsertStream(1, "general");
        InsertMessage(1, 1001, "general", "topic",
            plainText: "Click the link below",
            html: "<p>Click <a href=\"https://jira.hl7.org/browse/FHIR-99999\">here</a></p>");

        _indexer.RebuildFullIndex();

        using SqliteConnection conn = _db.OpenConnection();
        List<ZulipMessageTicketRecord> tickets = ZulipMessageTicketRecord.SelectList(conn, ZulipMessageId: 1001);

        Assert.Single(tickets);
        Assert.Equal("FHIR-99999", tickets[0].JiraKey);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void InsertStream(int zulipStreamId, string name)
    {
        using SqliteConnection conn = _db.OpenConnection();
        ZulipStreamRecord.Insert(conn, new ZulipStreamRecord
        {
            Id = ZulipStreamRecord.GetIndex(),
            ZulipStreamId = zulipStreamId,
            Name = name,
            Description = $"Description for {name}",
            IsWebPublic = true,
            MessageCount = 0,
            IncludeStream = true,
            LastFetchedAt = DateTimeOffset.UtcNow,
        });
    }

    private void InsertMessage(
        int streamId,
        int zulipMessageId,
        string streamName,
        string topic,
        string plainText,
        string? html = null,
        DateTimeOffset? ts = null)
    {
        using SqliteConnection conn = _db.OpenConnection();
        DateTimeOffset timestamp = ts ?? DateTimeOffset.UtcNow;
        ZulipMessageRecord.Insert(conn, new ZulipMessageRecord
        {
            Id = ZulipMessageRecord.GetIndex(),
            ZulipMessageId = zulipMessageId,
            StreamId = streamId,
            StreamName = streamName,
            Topic = topic,
            SenderId = zulipMessageId * 10,
            SenderName = "TestUser",
            SenderEmail = "test@example.com",
            ContentHtml = html ?? $"<p>{plainText}</p>",
            ContentPlain = plainText,
            Timestamp = timestamp,
            CreatedAt = DateTimeOffset.UtcNow,
            Reactions = null,
        });
    }
}
