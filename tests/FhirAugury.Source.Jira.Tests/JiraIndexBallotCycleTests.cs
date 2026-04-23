using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Jira.Tests;

/// <summary>
/// Phase 6 lock-in for <see cref="JiraIndexBuilder"/>'s ballot-cycles
/// rebuild path: groups <c>jira_ballot</c> by (BallotCycle, BallotCategory)
/// and bins <c>VoteBallot</c> values into per-disposition counters.
/// </summary>
public class JiraIndexBallotCycleTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;
    private readonly JiraIndexBuilder _builder;

    public JiraIndexBallotCycleTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_idx_bc_{Guid.NewGuid():N}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
        _builder = new JiraIndexBuilder(NullLogger<JiraIndexBuilder>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static JiraBallotRecord NewBallot(string key, string cycle, string category, string vote) => new JiraBallotRecord
    {
        Id = JiraBallotRecord.GetIndex(),
        Key = key,
        ProjectKey = "BALLOT",
        Title = $"vote {key}",
        Description = null,
        Summary = $"{vote} - someone (Org) : {cycle} | Pkg",
        Type = "Vote",
        Priority = "Major",
        Status = "Open",
        Assignee = null,
        Reporter = null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        ResolvedAt = null,
        VoteBallot = vote,
        BallotCategory = category,
        BallotCycle = cycle,
    };

    [Fact]
    public void RebuildIndexTables_GroupsBallotsByCycleAndCategory_AndBinsVotes()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            JiraBallotRecord.Insert(conn, NewBallot("BALLOT-1", "2024-Sep", "STU", "Affirmative"));
            JiraBallotRecord.Insert(conn, NewBallot("BALLOT-2", "2024-Sep", "STU", "Negative"));
            JiraBallotRecord.Insert(conn, NewBallot("BALLOT-3", "2024-Sep", "STU", "Negative-with-Comment"));
            JiraBallotRecord.Insert(conn, NewBallot("BALLOT-4", "2024-Sep", "STU", "Abstain"));
            JiraBallotRecord.Insert(conn, NewBallot("BALLOT-5", "2024-May", "Normative", "Affirmative"));
        }

        using (SqliteConnection conn = _db.OpenConnection())
        {
            _builder.RebuildIndexTables(conn);
        }

        using SqliteConnection check = _db.OpenConnection();
        List<JiraIndexBallotCycleRecord> rows = JiraIndexBallotCycleRecord.SelectList(check);

        Assert.Equal(2, rows.Count);

        JiraIndexBallotCycleRecord sep = rows.Single(r => r.BallotCycle == "2024-Sep");
        Assert.Equal("STU", sep.BallotLevel);
        Assert.Equal(4, sep.IssueCount);
        Assert.Equal(1, sep.AffirmativeVotes);
        Assert.Equal(1, sep.NegativeVotes);
        Assert.Equal(1, sep.NegativeWithCommentVotes);
        Assert.Equal(1, sep.AbstainVotes);

        JiraIndexBallotCycleRecord may = rows.Single(r => r.BallotCycle == "2024-May");
        Assert.Equal("Normative", may.BallotLevel);
        Assert.Equal(1, may.IssueCount);
        Assert.Equal(1, may.AffirmativeVotes);
        Assert.Equal(0, may.NegativeVotes);
    }
}
