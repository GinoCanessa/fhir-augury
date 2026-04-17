using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Jira.Tests;

public class JiraIndexBuilderUserTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;
    private readonly JiraIndexBuilder _builder;

    public JiraIndexBuilderUserTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_idx_test_{Guid.NewGuid()}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
        _builder = new JiraIndexBuilder(NullLogger<JiraIndexBuilder>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private JiraIssueRecord CreateIssue(SqliteConnection conn, string key,
        int? assigneeId = null, int? reporterId = null,
        int? voteMoverId = null, int? voteSeconderId = null)
    {
        JiraIssueRecord issue = new()
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = key,
            ProjectKey = "FHIR",
            Title = $"Issue {key}",
            Description = null,
            Summary = $"Issue {key}",
            Type = "Bug",
            Priority = "Major",
            Status = "Open",
            Resolution = null,
            ResolutionDescription = null,
            Assignee = null,
            Reporter = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ResolvedAt = null,
            WorkGroup = null,
            Specification = null,
            RaisedInVersion = null,
            SelectedBallot = null,
            RelatedArtifacts = null,
            RelatedIssues = null,
            DuplicateOf = null,
            AppliedVersions = null,
            ChangeType = null,
            Impact = null,
            Vote = null,
            Labels = null,
            CommentCount = 0,
            ChangeCategory = null,
            ChangeImpact = null,
            AssigneeId = assigneeId,
            ReporterId = reporterId,
            VoteMoverId = voteMoverId,
            VoteSeconderId = voteSeconderId,
        };
        JiraIssueRecord.Insert(conn, issue);
        return issue;
    }

    private JiraUserRecord CreateUser(SqliteConnection conn, string username, string displayName)
    {
        JiraUserRecord user = new()
        {
            Id = JiraUserRecord.GetIndex(),
            Username = username,
            DisplayName = displayName,
        };
        JiraUserRecord.Insert(conn, user);
        return user;
    }

    [Fact]
    public void RebuildUsersIndex_SingleRole_CountsCorrectly()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraUserRecord user = CreateUser(conn, "alice", "Alice");

        CreateIssue(conn, "FHIR-1", assigneeId: user.Id);
        CreateIssue(conn, "FHIR-2", assigneeId: user.Id);
        CreateIssue(conn, "FHIR-3", assigneeId: user.Id);

        _builder.RebuildIndexTables(conn);

        List<JiraIndexUserRecord> index = JiraIndexUserRecord.SelectList(conn);
        JiraIndexUserRecord? aliceEntry = index.FirstOrDefault(r => r.Name == "Alice");
        Assert.NotNull(aliceEntry);
        Assert.Equal(3, aliceEntry.IssueCount);
    }

    [Fact]
    public void RebuildUsersIndex_MultipleRoles_SameIssue_CountsOnce()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraUserRecord user = CreateUser(conn, "alice", "Alice");

        // Same user as both assignee and reporter on the same issue
        CreateIssue(conn, "FHIR-1", assigneeId: user.Id, reporterId: user.Id);

        _builder.RebuildIndexTables(conn);

        List<JiraIndexUserRecord> index = JiraIndexUserRecord.SelectList(conn);
        JiraIndexUserRecord? aliceEntry = index.FirstOrDefault(r => r.Name == "Alice");
        Assert.NotNull(aliceEntry);
        Assert.Equal(1, aliceEntry.IssueCount);
    }

    [Fact]
    public void RebuildUsersIndex_MultipleRoles_DifferentIssues_CountsAll()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraUserRecord user = CreateUser(conn, "alice", "Alice");

        CreateIssue(conn, "FHIR-1", assigneeId: user.Id);
        CreateIssue(conn, "FHIR-2", reporterId: user.Id);

        _builder.RebuildIndexTables(conn);

        List<JiraIndexUserRecord> index = JiraIndexUserRecord.SelectList(conn);
        JiraIndexUserRecord? aliceEntry = index.FirstOrDefault(r => r.Name == "Alice");
        Assert.NotNull(aliceEntry);
        Assert.Equal(2, aliceEntry.IssueCount);
    }

    [Fact]
    public void RebuildUsersIndex_NoIssues_ExcludesUser()
    {
        using SqliteConnection conn = _db.OpenConnection();
        CreateUser(conn, "alice", "Alice"); // No issue references

        _builder.RebuildIndexTables(conn);

        List<JiraIndexUserRecord> index = JiraIndexUserRecord.SelectList(conn);
        Assert.Empty(index);
    }

    [Fact]
    public void RebuildUsersIndex_IncludesInPersonRole()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraUserRecord user = CreateUser(conn, "alice", "Alice");
        JiraIssueRecord issue = CreateIssue(conn, "FHIR-1");

        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord()
        {
            Id = JiraIssueInPersonRecord.GetIndex(),
            IssueId = issue.Id,
            UserId = user.Id,
        });

        _builder.RebuildIndexTables(conn);

        List<JiraIndexUserRecord> index = JiraIndexUserRecord.SelectList(conn);
        JiraIndexUserRecord? aliceEntry = index.FirstOrDefault(r => r.Name == "Alice");
        Assert.NotNull(aliceEntry);
        Assert.Equal(1, aliceEntry.IssueCount);
    }

    [Fact]
    public void RebuildInPersonsIndex_MultipleIssues_CountsCorrectly()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraUserRecord user = CreateUser(conn, "alice", "Alice");
        JiraIssueRecord issue1 = CreateIssue(conn, "FHIR-1");
        JiraIssueRecord issue2 = CreateIssue(conn, "FHIR-2");
        JiraIssueRecord issue3 = CreateIssue(conn, "FHIR-3");

        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = issue1.Id, UserId = user.Id });
        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = issue2.Id, UserId = user.Id });
        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = issue3.Id, UserId = user.Id });

        _builder.RebuildIndexTables(conn);

        List<JiraIndexInPersonRecord> index = JiraIndexInPersonRecord.SelectList(conn);
        JiraIndexInPersonRecord? aliceEntry = index.FirstOrDefault(r => r.Name == "Alice");
        Assert.NotNull(aliceEntry);
        Assert.Equal(3, aliceEntry.IssueCount);
    }

    [Fact]
    public void RebuildInPersonsIndex_NoRecords_EmptyIndex()
    {
        using SqliteConnection conn = _db.OpenConnection();
        _builder.RebuildIndexTables(conn);

        List<JiraIndexInPersonRecord> index = JiraIndexInPersonRecord.SelectList(conn);
        Assert.Empty(index);
    }

    [Fact]
    public void RebuildIndexTables_CollapsesUsersWithSameDisplayName()
    {
        using SqliteConnection conn = _db.OpenConnection();

        // Real account and a synthetic mapper-inserted row that collides on DisplayName.
        JiraUserRecord real = CreateUser(conn, "jane.doe", "Jane Doe");
        JiraUserRecord synthetic = CreateUser(conn, "Jane Doe", "Jane Doe");

        // Issue A: reporter = real
        CreateIssue(conn, "FHIR-1", reporterId: real.Id);
        // Issue B: vote mover = synthetic
        CreateIssue(conn, "FHIR-2", voteMoverId: synthetic.Id);
        // Issue C: both real (reporter) and synthetic (vote seconder) -> counts once
        CreateIssue(conn, "FHIR-3", reporterId: real.Id, voteSeconderId: synthetic.Id);

        _builder.RebuildIndexTables(conn);

        List<JiraIndexUserRecord> index = JiraIndexUserRecord.SelectList(conn);
        List<JiraIndexUserRecord> janeRows = index.Where(r => r.Name == "Jane Doe").ToList();
        Assert.Single(janeRows);
        Assert.Equal(3, janeRows[0].IssueCount);
    }

    [Fact]
    public void RebuildIndexTables_CollapsesInPersonsWithSameDisplayName()
    {
        using SqliteConnection conn = _db.OpenConnection();

        JiraUserRecord real = CreateUser(conn, "jane.doe", "Jane Doe");
        JiraUserRecord synthetic = CreateUser(conn, "Jane Doe", "Jane Doe");

        JiraIssueRecord issue1 = CreateIssue(conn, "FHIR-1");
        JiraIssueRecord issue2 = CreateIssue(conn, "FHIR-2");
        JiraIssueRecord issue3 = CreateIssue(conn, "FHIR-3");

        // real on issue1, synthetic on issue2, both on issue3 (overlap -> count once)
        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = issue1.Id, UserId = real.Id });
        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = issue2.Id, UserId = synthetic.Id });
        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = issue3.Id, UserId = real.Id });
        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = issue3.Id, UserId = synthetic.Id });

        _builder.RebuildIndexTables(conn);

        List<JiraIndexInPersonRecord> index = JiraIndexInPersonRecord.SelectList(conn);
        List<JiraIndexInPersonRecord> janeRows = index.Where(r => r.Name == "Jane Doe").ToList();
        Assert.Single(janeRows);
        Assert.Equal(3, janeRows[0].IssueCount);
    }

    [Fact]
    public void RebuildInPersonsIndex_MultipleUsers_EachCounted()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraUserRecord alice = CreateUser(conn, "alice", "Alice");
        JiraUserRecord bob = CreateUser(conn, "bob", "Bob");
        JiraIssueRecord issue1 = CreateIssue(conn, "FHIR-1");
        JiraIssueRecord issue2 = CreateIssue(conn, "FHIR-2");

        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = issue1.Id, UserId = alice.Id });
        JiraIssueInPersonRecord.Insert(conn, new JiraIssueInPersonRecord() { Id = JiraIssueInPersonRecord.GetIndex(), IssueId = issue2.Id, UserId = bob.Id });

        _builder.RebuildIndexTables(conn);

        List<JiraIndexInPersonRecord> index = JiraIndexInPersonRecord.SelectList(conn);
        Assert.Equal(2, index.Count);
    }
}
