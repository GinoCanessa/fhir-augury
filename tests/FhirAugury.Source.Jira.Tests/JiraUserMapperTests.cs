using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Jira.Tests;

public class JiraUserMapperTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;
    private readonly JiraUserMapper _mapper;

    public JiraUserMapperTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_mapper_test_{Guid.NewGuid()}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
        _mapper = new JiraUserMapper();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void ResolveUser_NewUser_InsertsAndReturnsId()
    {
        using SqliteConnection conn = _db.OpenConnection();
        int? id = _mapper.ResolveUser(conn, "alice", "Alice A");

        Assert.NotNull(id);

        JiraUserRecord? record = JiraUserRecord.SelectSingle(conn, Username: "alice");
        Assert.NotNull(record);
        Assert.Equal("Alice A", record.DisplayName);
        Assert.Equal(id.Value, record.Id);
    }

    [Fact]
    public void ResolveUser_ExistingUser_ReturnsCachedId()
    {
        using SqliteConnection conn = _db.OpenConnection();
        int? id1 = _mapper.ResolveUser(conn, "alice", "Alice A");
        int? id2 = _mapper.ResolveUser(conn, "alice", "Alice A");

        Assert.Equal(id1, id2);

        // Should be only one user record
        List<JiraUserRecord> all = JiraUserRecord.SelectList(conn);
        Assert.Single(all);
    }

    [Fact]
    public void ResolveUser_UpdatesDisplayName_WhenChanged()
    {
        using SqliteConnection conn = _db.OpenConnection();
        _mapper.ResolveUser(conn, "alice", "Alice A");
        _mapper.ClearCache(); // Force DB lookup
        _mapper.ResolveUser(conn, "alice", "Alice B");

        JiraUserRecord? record = JiraUserRecord.SelectSingle(conn, Username: "alice");
        Assert.NotNull(record);
        Assert.Equal("Alice B", record.DisplayName);
    }

    [Fact]
    public void ResolveUser_BothNull_ReturnsNull()
    {
        using SqliteConnection conn = _db.OpenConnection();
        int? id = _mapper.ResolveUser(conn, null, null);

        Assert.Null(id);
    }

    [Fact]
    public void ResolveUser_UsernameOnly_UsesAsDisplayName()
    {
        using SqliteConnection conn = _db.OpenConnection();
        int? id = _mapper.ResolveUser(conn, "alice", null);

        Assert.NotNull(id);

        JiraUserRecord? record = JiraUserRecord.SelectSingle(conn, Username: "alice");
        Assert.NotNull(record);
        Assert.Equal("alice", record.DisplayName);
    }

    [Fact]
    public void ResolveUser_DisplayNameOnly_UsesAsSyntheticUsername()
    {
        using SqliteConnection conn = _db.OpenConnection();
        int? id = _mapper.ResolveUser(conn, null, "Alice A");

        Assert.NotNull(id);

        JiraUserRecord? record = JiraUserRecord.SelectSingle(conn, Username: "Alice A");
        Assert.NotNull(record);
        Assert.Equal("Alice A", record.DisplayName);
    }

    [Fact]
    public void ResolveByDisplayName_NewUser_CreatesWithSyntheticUsername()
    {
        using SqliteConnection conn = _db.OpenConnection();
        int? id = _mapper.ResolveByDisplayName(conn, "Bob B");

        Assert.NotNull(id);

        JiraUserRecord? record = JiraUserRecord.SelectSingle(conn, Username: "Bob B");
        Assert.NotNull(record);
        Assert.Equal("Bob B", record.DisplayName);
    }

    [Fact]
    public void ResolveByDisplayName_ExistingByDisplayName_ReturnsId()
    {
        using SqliteConnection conn = _db.OpenConnection();
        int? id1 = _mapper.ResolveByDisplayName(conn, "Bob B");
        int? id2 = _mapper.ResolveByDisplayName(conn, "Bob B");

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ResolveByDisplayName_Null_ReturnsNull()
    {
        using SqliteConnection conn = _db.OpenConnection();
        int? id = _mapper.ResolveByDisplayName(conn, null);

        Assert.Null(id);
    }

    [Fact]
    public void ResolveByDisplayName_Whitespace_ReturnsNull()
    {
        using SqliteConnection conn = _db.OpenConnection();
        int? id = _mapper.ResolveByDisplayName(conn, "   ");

        Assert.Null(id);
    }

    [Fact]
    public void ClearCache_ForcesDbLookup()
    {
        using SqliteConnection conn = _db.OpenConnection();
        int? id1 = _mapper.ResolveUser(conn, "alice", "Alice A");

        _mapper.ClearCache();

        // After clearing cache, the mapper should still find the user in the DB
        int? id2 = _mapper.ResolveUser(conn, "alice", "Alice A");

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ResolveUser_WhitespaceInputs_Trimmed()
    {
        using SqliteConnection conn = _db.OpenConnection();
        int? id = _mapper.ResolveUser(conn, "  alice  ", "  Alice A  ");

        Assert.NotNull(id);

        JiraUserRecord? record = JiraUserRecord.SelectSingle(conn, Username: "alice");
        Assert.NotNull(record);
        Assert.Equal("Alice A", record.DisplayName);
    }

    [Fact]
    public void ResolveUser_MultipleUsers_DifferentIds()
    {
        using SqliteConnection conn = _db.OpenConnection();
        int? id1 = _mapper.ResolveUser(conn, "alice", "Alice");
        int? id2 = _mapper.ResolveUser(conn, "bob", "Bob");

        Assert.NotNull(id1);
        Assert.NotNull(id2);
        Assert.NotEqual(id1, id2);
    }
}
