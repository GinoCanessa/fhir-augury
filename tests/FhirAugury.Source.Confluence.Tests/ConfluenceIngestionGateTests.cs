using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using FhirAugury.Source.Confluence.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Confluence.Tests;

/// <summary>
/// Pins the durability of the ingestion gate: a block outlives the object that
/// recorded it, outlives a cache rebuild, and ends only when a human clears it.
/// </summary>
/// <remarks>
/// <see cref="Block_SurvivesResetDatabase"/> is the load-bearing one.
/// <c>RebuildFromCacheAsync</c> calls <c>ResetDatabase()</c>, and a rebuild that
/// silently reopened the gate would send the service straight back into a
/// hostile WAF.
/// </remarks>
public class ConfluenceIngestionGateTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private ConfluenceDatabase _database;

    public ConfluenceIngestionGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"confluence-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "confluence.db");
        _database = Open();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private ConfluenceDatabase Open()
    {
        ConfluenceDatabase database = new(_dbPath, NullLogger<ConfluenceDatabase>.Instance);
        database.Initialize();
        return database;
    }

    private ConfluenceIngestionGate Gate() =>
        new(_database, NullLogger<ConfluenceIngestionGate>.Instance);

    private static ConfluenceHumanInterventionRequiredException Challenge(
        string action = "captcha", string url = "https://confluence.test/rest/api/content") =>
        new(405, "Not Allowed", action, url);

    [Fact]
    public void NewGate_IsNotBlocked()
    {
        ConfluenceIngestionGate gate = Gate();

        Assert.False(gate.IsBlocked);
        Assert.Null(gate.Current);
    }

    [Fact]
    public void Block_PersistsAcrossGateRecreation()
    {
        Gate().Block(Challenge(url: "https://confluence.test/rest/api/space"));

        ConfluenceIngestionGate reloaded = Gate();

        Assert.True(reloaded.IsBlocked);
        ConfluenceIngestionBlockRecord current = reloaded.Current!;
        Assert.Equal(405, current.HttpStatus);
        Assert.Equal("Not Allowed", current.ReasonPhrase);
        Assert.Equal("https://confluence.test/rest/api/space", current.RequestUrl);
        Assert.Contains("x-amzn-waf-action", current.Fingerprint!, StringComparison.Ordinal);
        Assert.Contains("captcha", current.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Clear_ReopensTheGate()
    {
        ConfluenceIngestionGate gate = Gate();
        gate.Block(Challenge());

        Assert.True(gate.Clear("gino"));
        Assert.False(gate.IsBlocked);
        Assert.Equal("gino", gate.Current!.ClearedBy);
        Assert.NotNull(gate.Current.ClearedAt);

        // And the reopen is durable too, not just in-memory.
        Assert.False(Gate().IsBlocked);
    }

    [Fact]
    public void Clear_WhenNotBlocked_ReturnsFalse()
    {
        ConfluenceIngestionGate gate = Gate();

        Assert.False(gate.Clear("gino"));

        gate.Block(Challenge());
        Assert.True(gate.Clear(null));
        Assert.False(gate.Clear(null));
    }

    [Fact]
    public void Block_IsIdempotent_PreservesOriginalBlockedAt()
    {
        ConfluenceIngestionGate gate = Gate();
        gate.Block(Challenge(url: "https://confluence.test/first"));

        DateTimeOffset first = gate.Current!.BlockedAt;

        gate.Block(Challenge(action: "block", url: "https://confluence.test/second"));

        Assert.Equal(first, gate.Current!.BlockedAt);
        Assert.Equal("https://confluence.test/first", gate.Current.RequestUrl);
    }

    [Fact]
    public void Block_SurvivesResetDatabase()
    {
        Gate().Block(Challenge());

        // What RebuildFromCacheAsync does before replaying the cache.
        _database.ResetDatabase();

        Assert.True(Gate().IsBlocked);
    }

    [Fact]
    public void Block_SurvivesAReopenedDatabase()
    {
        Gate().Block(Challenge());

        _database.Dispose();
        SqliteConnection.ClearAllPools();
        _database = Open();

        Assert.True(Gate().IsBlocked);
    }
}
