using FhirAugury.Common.Database.Records;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Database.Records;
using FhirAugury.Source.Zulip.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Zulip.Tests;

public class ZulipXRefRebuilderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ZulipDatabase _db;
    private readonly ZulipXRefRebuilder _indexer;

    public ZulipXRefRebuilderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zulip_ticket_test_{Guid.NewGuid()}.db");
        _db = new ZulipDatabase(_dbPath, NullLogger<ZulipDatabase>.Instance);
        _db.Initialize();
        _indexer = new ZulipXRefRebuilder(_db, NullLogger<ZulipXRefRebuilder>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    // ── Full Rebuild ─────────────────────────────────────────────────

    [Fact]
    public void RebuildAll_IndexesMessageTickets()
    {
        InsertStream(1, "general");
        InsertMessage(1, 1001, "general", "ballot", "See FHIR-43499 for the resolution");

        _indexer.RebuildAll();

        using SqliteConnection conn = _db.OpenConnection();
        List<JiraXRefRecord> refs = JiraXRefRecord.SelectList(conn);

        Assert.Single(refs);
        Assert.Equal("1001", refs[0].SourceId);
        Assert.Equal("FHIR-43499", refs[0].JiraKey);
    }

    [Fact]
    public void RebuildAll_AggregatesThreadTickets()
    {
        InsertStream(1, "general");
        InsertMessage(1, 1001, "general", "ballot", "See FHIR-100 for details");
        InsertMessage(1, 1002, "general", "ballot", "Also FHIR-100 is relevant");
        InsertMessage(1, 1003, "general", "ballot", "And FHIR-100 again");

        _indexer.RebuildAll();

        using SqliteConnection conn = _db.OpenConnection();
        List<ZulipThreadTicketRecord> threads = ZulipThreadTicketRecord.SelectList(conn);

        Assert.Single(threads);
        Assert.Equal("general", threads[0].StreamName);
        Assert.Equal("ballot", threads[0].Topic);
        Assert.Equal("FHIR-100", threads[0].JiraKey);
        Assert.Equal(3, threads[0].ReferenceCount);
    }

    [Fact]
    public void RebuildAll_ClearsExistingData()
    {
        InsertStream(1, "general");
        InsertMessage(1, 1001, "general", "ballot", "FHIR-100 mentioned");

        _indexer.RebuildAll();
        _indexer.RebuildAll();

        using SqliteConnection conn = _db.OpenConnection();
        List<JiraXRefRecord> refs = JiraXRefRecord.SelectList(conn);

        Assert.Single(refs);
    }

    [Fact]
    public void RebuildAll_MultiplePatternsInOneMessage()
    {
        InsertStream(1, "general");
        InsertMessage(1, 1001, "general", "ballot", "FHIR-100, J#200, and GF#300 all relevant");

        _indexer.RebuildAll();

        using SqliteConnection conn = _db.OpenConnection();
        List<JiraXRefRecord> refs = JiraXRefRecord.SelectList(conn, SourceId: "1001");

        Assert.Equal(3, refs.Count);
        Assert.Contains(refs, t => t.JiraKey == "FHIR-100");
        Assert.Contains(refs, t => t.JiraKey == "FHIR-200");
        Assert.Contains(refs, t => t.JiraKey == "GF-300");
    }

    // ── Thread Aggregation ───────────────────────────────────────────

    [Fact]
    public void ThreadTicket_ReferenceCount_Correct()
    {
        InsertStream(1, "general");
        InsertMessage(1, 1001, "general", "topic", "FHIR-100 first", ts: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        InsertMessage(1, 1002, "general", "topic", "FHIR-100 second", ts: new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero));
        InsertMessage(1, 1003, "general", "topic", "FHIR-100 third", ts: new DateTimeOffset(2024, 1, 3, 0, 0, 0, TimeSpan.Zero));

        _indexer.RebuildAll();

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

        _indexer.RebuildAll();

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

        _indexer.RebuildAll();

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

        _indexer.RebuildAll();

        using SqliteConnection conn = _db.OpenConnection();
        List<JiraXRefRecord> refs = JiraXRefRecord.SelectList(conn, SourceId: "1001");

        Assert.Single(refs);
        Assert.Equal("FHIR-99999", refs[0].JiraKey);
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
            BaselineValue = 5,
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
