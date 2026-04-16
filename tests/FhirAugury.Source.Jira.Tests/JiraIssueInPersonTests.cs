using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Jira.Tests;

public class JiraIssueInPersonTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;

    public JiraIssueInPersonTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_inperson_test_{Guid.NewGuid()}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void InsertAndSelect_InPersonRecord_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();

        // Create prerequisite user
        JiraUserRecord user = new() { Id = JiraUserRecord.GetIndex(), Username = "alice", DisplayName = "Alice" };
        JiraUserRecord.Insert(conn, user);

        JiraIssueInPersonRecord record = new()
        {
            Id = JiraIssueInPersonRecord.GetIndex(),
            IssueId = 1,
            UserId = user.Id,
        };
        JiraIssueInPersonRecord.Insert(conn, record);

        List<JiraIssueInPersonRecord> results = JiraIssueInPersonRecord.SelectList(conn, IssueId: 1);
        Assert.Single(results);
        Assert.Equal(user.Id, results[0].UserId);
    }

    [Fact]
    public void Insert_MultipleUsersForSameIssue_AllStored()
    {
        using SqliteConnection conn = _db.OpenConnection();

        JiraUserRecord u1 = new() { Id = JiraUserRecord.GetIndex(), Username = "alice", DisplayName = "Alice" };
        JiraUserRecord u2 = new() { Id = JiraUserRecord.GetIndex(), Username = "bob", DisplayName = "Bob" };
        JiraUserRecord u3 = new() { Id = JiraUserRecord.GetIndex(), Username = "charlie", DisplayName = "Charlie" };
        JiraUserRecord.Insert(conn, u1);
        JiraUserRecord.Insert(conn, u2);
        JiraUserRecord.Insert(conn, u3);

        int issueId = 42;
        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = issueId, UserId = u1.Id });
        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = issueId, UserId = u2.Id });
        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = issueId, UserId = u3.Id });

        List<JiraIssueInPersonRecord> results = JiraIssueInPersonRecord.SelectList(conn, IssueId: issueId);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Insert_SameUserMultipleIssues_AllStored()
    {
        using SqliteConnection conn = _db.OpenConnection();

        JiraUserRecord user = new() { Id = JiraUserRecord.GetIndex(), Username = "alice", DisplayName = "Alice" };
        JiraUserRecord.Insert(conn, user);

        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = 1, UserId = user.Id });
        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = 2, UserId = user.Id });

        List<JiraIssueInPersonRecord> byUser = JiraIssueInPersonRecord.SelectList(conn, UserId: user.Id);
        Assert.Equal(2, byUser.Count);
    }

    [Fact]
    public void SelectList_ByIssueId_FiltersCorrectly()
    {
        using SqliteConnection conn = _db.OpenConnection();

        JiraUserRecord u1 = new() { Id = JiraUserRecord.GetIndex(), Username = "alice", DisplayName = "Alice" };
        JiraUserRecord u2 = new() { Id = JiraUserRecord.GetIndex(), Username = "bob", DisplayName = "Bob" };
        JiraUserRecord.Insert(conn, u1);
        JiraUserRecord.Insert(conn, u2);

        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = 1, UserId = u1.Id });
        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = 2, UserId = u2.Id });

        List<JiraIssueInPersonRecord> issue1 = JiraIssueInPersonRecord.SelectList(conn, IssueId: 1);
        Assert.Single(issue1);
        Assert.Equal(u1.Id, issue1[0].UserId);
    }
}
