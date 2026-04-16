using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Jira.Tests;

public class JiraUserRecordTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;

    public JiraUserRecordTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_user_test_{Guid.NewGuid()}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void InsertAndSelect_JiraUser_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraUserRecord user = new()
        {
            Id = JiraUserRecord.GetIndex(),
            Username = "alice.smith",
            DisplayName = "Alice Smith",
        };

        JiraUserRecord.Insert(conn, user);
        JiraUserRecord? result = JiraUserRecord.SelectSingle(conn, Username: "alice.smith");

        Assert.NotNull(result);
        Assert.Equal("alice.smith", result.Username);
        Assert.Equal("Alice Smith", result.DisplayName);
    }

    [Fact]
    public void Insert_DuplicateUsername_IgnoredWithFlag()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraUserRecord user1 = new()
        {
            Id = JiraUserRecord.GetIndex(),
            Username = "bob",
            DisplayName = "Bob One",
        };
        JiraUserRecord user2 = new()
        {
            Id = JiraUserRecord.GetIndex(),
            Username = "bob",
            DisplayName = "Bob Two",
        };

        JiraUserRecord.Insert(conn, user1);
        JiraUserRecord.Insert(conn, user2, ignoreDuplicates: true);

        // Only one record should exist
        List<JiraUserRecord> all = JiraUserRecord.SelectList(conn);
        Assert.Single(all);
        Assert.Equal("Bob One", all[0].DisplayName);
    }

    [Fact]
    public void Update_ChangesDisplayName()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraUserRecord user = new()
        {
            Id = JiraUserRecord.GetIndex(),
            Username = "charlie",
            DisplayName = "Charlie C",
        };

        JiraUserRecord.Insert(conn, user);

        user.DisplayName = "Charles C";
        JiraUserRecord.Update(conn, user);

        JiraUserRecord? result = JiraUserRecord.SelectSingle(conn, Username: "charlie");
        Assert.NotNull(result);
        Assert.Equal("Charles C", result.DisplayName);
    }

    [Fact]
    public void SelectList_ReturnsAllUsers()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraUserRecord.Insert(conn, new JiraUserRecord() { Id = JiraUserRecord.GetIndex(), Username = "u1", DisplayName = "User 1" });
        JiraUserRecord.Insert(conn, new JiraUserRecord() { Id = JiraUserRecord.GetIndex(), Username = "u2", DisplayName = "User 2" });
        JiraUserRecord.Insert(conn, new JiraUserRecord() { Id = JiraUserRecord.GetIndex(), Username = "u3", DisplayName = "User 3" });

        List<JiraUserRecord> all = JiraUserRecord.SelectList(conn);
        Assert.Equal(3, all.Count);
    }
}
