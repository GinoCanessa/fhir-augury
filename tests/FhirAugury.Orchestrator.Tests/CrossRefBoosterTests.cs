using FhirAugury.Orchestrator.Database;
using FhirAugury.Orchestrator.Database.Records;
using FhirAugury.Orchestrator.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Orchestrator.Tests;

public class CrossRefBoosterTests : IDisposable
{
    private readonly string _dbPath;
    private readonly OrchestratorDatabase _db;

    public CrossRefBoosterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"orch_test_{Guid.NewGuid()}.db");
        _db = new OrchestratorDatabase(_dbPath, NullLogger<OrchestratorDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Boost_NoXrefs_ScoreUnchanged()
    {
        var booster = new CrossRefBooster(_db);
        var items = new List<ScoredItem>
        {
            MakeItem("jira", "FHIR-1", 0.8),
        };

        var result = booster.Boost(items, boostFactor: 0.5);

        // No xrefs → log(1 + 0) = 0 → boosted = 0.8 * (1 + 0.5 * 0) = 0.8
        Assert.Equal(0.8, result[0].Score, precision: 5);
    }

    [Fact]
    public void Boost_WithXrefs_ScoreIncreased()
    {
        // Seed cross-references
        using var conn = _db.OpenConnection();
        CrossRefLinkRecord.Insert(conn, MakeXref("zulip", "msg:100", "jira", "FHIR-1"));
        CrossRefLinkRecord.Insert(conn, MakeXref("zulip", "msg:200", "jira", "FHIR-1"));
        conn.Close();

        var booster = new CrossRefBooster(_db);
        var items = new List<ScoredItem>
        {
            MakeItem("jira", "FHIR-1", 0.5),
        };

        var result = booster.Boost(items, boostFactor: 0.5);

        // 2 incoming xrefs → boosted = 0.5 * (1 + 0.5 * ln(3)) ≈ 0.5 * 1.549 ≈ 0.775
        Assert.True(result[0].Score > 0.5, "Score should be boosted above original");
    }

    [Fact]
    public void Boost_CountsBothDirections()
    {
        using var conn = _db.OpenConnection();
        // Outgoing: FHIR-1 → Zulip msg:100
        CrossRefLinkRecord.Insert(conn, MakeXref("jira", "FHIR-1", "zulip", "msg:100"));
        // Incoming: Zulip msg:200 → FHIR-1
        CrossRefLinkRecord.Insert(conn, MakeXref("zulip", "msg:200", "jira", "FHIR-1"));
        conn.Close();

        var booster = new CrossRefBooster(_db);
        var items = new List<ScoredItem>
        {
            MakeItem("jira", "FHIR-1", 1.0),
        };

        var result = booster.Boost(items, boostFactor: 0.5);

        // 2 total xrefs (1 outgoing + 1 incoming)
        Assert.True(result[0].Score > 1.0);
    }

    [Fact]
    public void Boost_ZeroBoostFactor_ScoreUnchanged()
    {
        using var conn = _db.OpenConnection();
        CrossRefLinkRecord.Insert(conn, MakeXref("zulip", "msg:1", "jira", "FHIR-1"));
        conn.Close();

        var booster = new CrossRefBooster(_db);
        var items = new List<ScoredItem>
        {
            MakeItem("jira", "FHIR-1", 0.7),
        };

        var result = booster.Boost(items, boostFactor: 0.0);

        Assert.Equal(0.7, result[0].Score, precision: 5);
    }

    private static ScoredItem MakeItem(string source, string id, double score) => new()
    {
        Source = source,
        Id = id,
        Title = "Title",
        Snippet = "snippet",
        Score = score,
        Url = "https://example.com",
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static CrossRefLinkRecord MakeXref(
        string sourceType, string sourceId,
        string targetType, string targetId) => new()
    {
        Id = CrossRefLinkRecord.GetIndex(),
        SourceType = sourceType,
        SourceId = sourceId,
        TargetType = targetType,
        TargetId = targetId,
        LinkType = "mentions",
        Context = "test context",
        DiscoveredAt = DateTimeOffset.UtcNow,
    };
}
