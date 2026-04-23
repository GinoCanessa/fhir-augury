using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Controllers;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Indexing;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Tests;

public class LocalProcessingControllerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;
    private readonly LocalProcessingController _controller;

    public LocalProcessingControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_lp_ctrl_test_{Guid.NewGuid()}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
        _controller = new LocalProcessingController(_db, Options.Create(new JiraServiceOptions()));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private JiraIssueRecord SeedIssue(
        SqliteConnection conn,
        string key,
        string projectKey = "FHIR",
        DateTimeOffset? processedAt = null,
        string? relatedArtifacts = null,
        string? status = "Open",
        string? reporter = null)
    {
        JiraIssueRecord issue = new()
        {
            Id = JiraIssueRecord.GetIndex(),
            Key = key,
            ProjectKey = projectKey,
            Title = $"Issue {key}",
            Description = null,
            Summary = null,
            Type = "Bug",
            Priority = "Major",
            Status = status ?? "Open",
            Resolution = null,
            ResolutionDescription = null,
            Assignee = null,
            Reporter = reporter,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ResolvedAt = null,
            WorkGroup = null,
            Specification = null,
            RaisedInVersion = null,
            SelectedBallot = null,
            RelatedArtifacts = relatedArtifacts,
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
            ProcessedLocallyAt = processedAt,
        };
        JiraIssueRecord.Insert(conn, issue);
        return issue;
    }

    private static JiraLocalProcessingListResponse UnwrapList(IActionResult result) =>
        Assert.IsType<JiraLocalProcessingListResponse>(Assert.IsType<OkObjectResult>(result).Value);

    private static JiraIssueSummaryEntry UnwrapSingle(IActionResult result) =>
        Assert.IsType<JiraIssueSummaryEntry>(Assert.IsType<OkObjectResult>(result).Value);

    private static JiraLocalProcessingSetResponse UnwrapSet(IActionResult result) =>
        Assert.IsType<JiraLocalProcessingSetResponse>(Assert.IsType<OkObjectResult>(result).Value);

    private static JiraLocalProcessingClearResponse UnwrapClear(IActionResult result) =>
        Assert.IsType<JiraLocalProcessingClearResponse>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public void GetTickets_EmptyFilter_ReturnsAllOrderedByKey()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-3");
            SeedIssue(conn, "FHIR-1");
            SeedIssue(conn, "FHIR-2");
        }

        JiraLocalProcessingListResponse response = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest()));

        Assert.Equal(3, response.Total);
        Assert.Equal(JiraLocalProcessingQueryBuilder.DefaultLimit, response.Limit);
        Assert.Equal(0, response.Offset);
        Assert.Equal(["FHIR-1", "FHIR-2", "FHIR-3"], response.Results.Select(r => r.Key).ToArray());
    }

    [Fact]
    public void GetTickets_Paging_ReturnsRequestedPageAndUnpagedTotal()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            for (int i = 1; i <= 5; i++) SeedIssue(conn, $"FHIR-{i}");
        }

        JiraLocalProcessingListResponse response = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { Limit = 2, Offset = 2 }));

        Assert.Equal(5, response.Total);
        Assert.Equal(2, response.Limit);
        Assert.Equal(2, response.Offset);
        Assert.Equal(["FHIR-3", "FHIR-4"], response.Results.Select(r => r.Key).ToArray());
    }

    [Fact]
    public void GetTickets_ProjectsFilter_OrJoinsWithinField()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1", projectKey: "FHIR");
            SeedIssue(conn, "XYZ-1", projectKey: "XYZ");
            SeedIssue(conn, "OTHER-1", projectKey: "OTHER");
        }

        JiraLocalProcessingListResponse response = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { Projects = ["FHIR", "XYZ"] }));

        Assert.Equal(2, response.Total);
        Assert.Equal(["FHIR-1", "XYZ-1"], response.Results.Select(r => r.Key).OrderBy(s => s).ToArray());
    }

    [Fact]
    public void GetTickets_ProcessedLocallyFilter_RespectsTriState()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1", processedAt: DateTimeOffset.UtcNow);
            SeedIssue(conn, "FHIR-2", processedAt: null);
            SeedIssue(conn, "FHIR-3", processedAt: DateTimeOffset.UtcNow);
        }

        JiraLocalProcessingListResponse onlyProcessed = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { ProcessedLocally = true }));
        Assert.Equal(2, onlyProcessed.Total);

        JiraLocalProcessingListResponse onlyUnprocessed = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { ProcessedLocally = false }));
        Assert.Equal(1, onlyUnprocessed.Total);
        Assert.Equal("FHIR-2", onlyUnprocessed.Results.Single().Key);

        JiraLocalProcessingListResponse all = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { ProcessedLocally = null }));
        Assert.Equal(3, all.Total);
    }

    [Fact]
    public void GetTickets_RelatedArtifacts_CaseInsensitiveSubstring()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1", relatedArtifacts: "US Core, IPS");
            SeedIssue(conn, "FHIR-2", relatedArtifacts: "SDC");
            SeedIssue(conn, "FHIR-3", relatedArtifacts: null);
        }

        JiraLocalProcessingListResponse coreOnly = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { RelatedArtifacts = ["core"] }));
        Assert.Equal(["FHIR-1"], coreOnly.Results.Select(r => r.Key).ToArray());

        JiraLocalProcessingListResponse both = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { RelatedArtifacts = ["core", "sdc"] }));
        Assert.Equal(["FHIR-1", "FHIR-2"], both.Results.Select(r => r.Key).OrderBy(s => s).ToArray());
    }

    [Fact]
    public void GetTickets_Labels_OrSemantics()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            JiraIssueRecord i1 = SeedIssue(conn, "FHIR-1");
            JiraIssueRecord i2 = SeedIssue(conn, "FHIR-2");
            JiraIssueRecord i3 = SeedIssue(conn, "FHIR-3");

            JiraIndexLabelRecord l1 = new() { Id = JiraIndexLabelRecord.GetIndex(), Name = "L1", IssueCount = 1 };
            JiraIndexLabelRecord l2 = new() { Id = JiraIndexLabelRecord.GetIndex(), Name = "L2", IssueCount = 1 };
            JiraIndexLabelRecord l3 = new() { Id = JiraIndexLabelRecord.GetIndex(), Name = "L3", IssueCount = 1 };
            JiraIndexLabelRecord.Insert(conn, l1);
            JiraIndexLabelRecord.Insert(conn, l2);
            JiraIndexLabelRecord.Insert(conn, l3);

            JiraIssueLabelRecord.Insert(conn, new JiraIssueLabelRecord { Id = JiraIssueLabelRecord.GetIndex(), IssueKey = i1.Key, LabelId = l1.Id });
            JiraIssueLabelRecord.Insert(conn, new JiraIssueLabelRecord { Id = JiraIssueLabelRecord.GetIndex(), IssueKey = i2.Key, LabelId = l2.Id });
            JiraIssueLabelRecord.Insert(conn, new JiraIssueLabelRecord { Id = JiraIssueLabelRecord.GetIndex(), IssueKey = i3.Key, LabelId = l3.Id });
        }

        JiraLocalProcessingListResponse response = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { Labels = ["L1", "L2"] }));

        Assert.Equal(["FHIR-1", "FHIR-2"], response.Results.Select(r => r.Key).OrderBy(s => s).ToArray());
    }

    [Fact]
    public void GetRandomTicket_WithMatches_ReturnsOneOfThem()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1");
            SeedIssue(conn, "FHIR-2");
            SeedIssue(conn, "FHIR-3");
        }

        HashSet<string> seen = [];
        for (int i = 0; i < 20; i++)
        {
            JiraIssueSummaryEntry entry = UnwrapSingle(
                _controller.GetRandomTicket(new JiraLocalProcessingFilter()));
            Assert.Contains(entry.Key, new[] { "FHIR-1", "FHIR-2", "FHIR-3" });
            seen.Add(entry.Key);
        }

        Assert.True(seen.Count >= 2, "Expected at least two distinct random keys across 20 trials.");
    }

    [Fact]
    public void GetRandomTicket_NoMatches_Returns404()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1", processedAt: null);
        }

        IActionResult result = _controller.GetRandomTicket(
            new JiraLocalProcessingFilter { ProcessedLocally = true });
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void SetProcessed_True_StoresCurrentTimestamp()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1", processedAt: null);
        }

        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-5);
        JiraLocalProcessingSetResponse response = UnwrapSet(
            _controller.SetProcessed(new JiraLocalProcessingSetRequest { Key = "FHIR-1", ProcessedLocally = true }));
        DateTimeOffset after = DateTimeOffset.UtcNow.AddSeconds(5);

        Assert.Equal("FHIR-1", response.Key);
        Assert.False(response.PreviousValue);
        Assert.True(response.NewValue);

        using SqliteConnection conn2 = _db.OpenConnection();
        JiraIssueRecord? fetched = JiraIssueRecord.SelectList(conn2, Key: "FHIR-1").FirstOrDefault();
        Assert.NotNull(fetched);
        Assert.NotNull(fetched.ProcessedLocallyAt);
        Assert.InRange(fetched.ProcessedLocallyAt.Value, before, after);
    }

    [Fact]
    public void SetProcessed_FalseAndNull_BothClearTheColumn()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-A", processedAt: DateTimeOffset.UtcNow);
            SeedIssue(conn, "FHIR-B", processedAt: DateTimeOffset.UtcNow);
        }

        JiraLocalProcessingSetResponse r1 = UnwrapSet(
            _controller.SetProcessed(new JiraLocalProcessingSetRequest { Key = "FHIR-A", ProcessedLocally = false }));
        Assert.True(r1.PreviousValue);
        Assert.False(r1.NewValue);

        JiraLocalProcessingSetResponse r2 = UnwrapSet(
            _controller.SetProcessed(new JiraLocalProcessingSetRequest { Key = "FHIR-B", ProcessedLocally = null }));
        Assert.True(r2.PreviousValue);
        Assert.False(r2.NewValue);

        using SqliteConnection conn2 = _db.OpenConnection();
        Assert.Null(JiraIssueRecord.SelectList(conn2, Key: "FHIR-A").Single().ProcessedLocallyAt);
        Assert.Null(JiraIssueRecord.SelectList(conn2, Key: "FHIR-B").Single().ProcessedLocallyAt);
    }

    [Fact]
    public void SetProcessed_UnknownKey_Returns404AndDoesNotInsert()
    {
        IActionResult result = _controller.SetProcessed(
            new JiraLocalProcessingSetRequest { Key = "NOPE-1", ProcessedLocally = true });
        Assert.IsType<NotFoundResult>(result);

        using SqliteConnection conn = _db.OpenConnection();
        Assert.Empty(JiraIssueRecord.SelectList(conn, Key: "NOPE-1"));
    }

    // ── Phase 9 / 10: per-shape ?type= routing ─────────────────────────

    private static JiraProjectScopeStatementRecord NewPss(string key, DateTimeOffset? processedAt) => new JiraProjectScopeStatementRecord
    {
        Id = JiraProjectScopeStatementRecord.GetIndex(),
        Key = key,
        ProjectKey = "PSS",
        Title = $"PSS {key}",
        Description = null,
        Summary = null,
        Type = "Project Scope Statement",
        Priority = "Major",
        Status = "Open",
        Assignee = null,
        Reporter = null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        ResolvedAt = null,
        ProcessedLocallyAt = processedAt,
    };

    private static JiraBaldefRecord NewBaldef(string key, DateTimeOffset? processedAt) => new JiraBaldefRecord
    {
        Id = JiraBaldefRecord.GetIndex(),
        Key = key,
        ProjectKey = "BALDEF",
        Title = $"BALDEF {key}",
        Description = null,
        Summary = null,
        Type = "Ballot",
        Priority = "Major",
        Status = "Open",
        Assignee = null,
        Reporter = null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        ResolvedAt = null,
        ProcessedLocallyAt = processedAt,
    };

    private static JiraBallotRecord NewBallot(string key, DateTimeOffset? processedAt) => new JiraBallotRecord
    {
        Id = JiraBallotRecord.GetIndex(),
        Key = key,
        ProjectKey = "BALLOT",
        Title = $"BALLOT {key}",
        Description = null,
        Summary = null,
        Type = "Vote",
        Priority = "Major",
        Status = "Open",
        Assignee = null,
        Reporter = null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        ResolvedAt = null,
        ProcessedLocallyAt = processedAt,
    };

    [Fact]
    public void SetProcessed_PssTable_MarksOnlyPssRow()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            JiraProjectScopeStatementRecord.Insert(conn, NewPss("PSS-1", processedAt: null));
            SeedIssue(conn, "FHIR-1", processedAt: null);
        }

        JiraLocalProcessingSetResponse resp = UnwrapSet(
            _controller.SetProcessed(new JiraLocalProcessingSetRequest { Key = "PSS-1", ProcessedLocally = true }, type: "pss"));
        Assert.True(resp.NewValue);

        JiraLocalProcessingListResponse listed = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { ProcessedLocally = true }, type: "pss"));
        Assert.Equal(1, listed.Total);
        Assert.Equal("PSS-1", listed.Results[0].Key);

        // FHIR table should be untouched.
        JiraLocalProcessingListResponse fhir = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { ProcessedLocally = true }, type: "fhir"));
        Assert.Equal(0, fhir.Total);
    }

    [Fact]
    public void SetProcessed_BaldefTable_MarksOnlyBaldefRow()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            JiraBaldefRecord.Insert(conn, NewBaldef("BALDEF-1", processedAt: null));
        }

        JiraLocalProcessingSetResponse resp = UnwrapSet(
            _controller.SetProcessed(new JiraLocalProcessingSetRequest { Key = "BALDEF-1", ProcessedLocally = true }, type: "baldef"));
        Assert.True(resp.NewValue);

        JiraLocalProcessingListResponse listed = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { ProcessedLocally = true }, type: "baldef"));
        Assert.Equal(1, listed.Total);
        Assert.Equal("BALDEF-1", listed.Results[0].Key);
    }

    [Fact]
    public void SetProcessed_BallotTable_MarksOnlyBallotRow()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            JiraBallotRecord.Insert(conn, NewBallot("BALLOT-1", processedAt: null));
        }

        JiraLocalProcessingSetResponse resp = UnwrapSet(
            _controller.SetProcessed(new JiraLocalProcessingSetRequest { Key = "BALLOT-1", ProcessedLocally = true }, type: "ballot"));
        Assert.True(resp.NewValue);

        JiraLocalProcessingListResponse listed = UnwrapList(
            _controller.GetTickets(new JiraLocalProcessingListRequest { ProcessedLocally = true }, type: "ballot"));
        Assert.Equal(1, listed.Total);
        Assert.Equal("BALLOT-1", listed.Results[0].Key);
    }

    [Fact]
    public void ClearAllProcessed_NoType_ClearsAcrossAllFourTables()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1", processedAt: now);
            JiraProjectScopeStatementRecord.Insert(conn, NewPss("PSS-1", processedAt: now));
            JiraBaldefRecord.Insert(conn, NewBaldef("BALDEF-1", processedAt: now));
            JiraBallotRecord.Insert(conn, NewBallot("BALLOT-1", processedAt: now));
        }

        JiraLocalProcessingClearResponse resp = UnwrapClear(_controller.ClearAllProcessed());
        Assert.Equal(4, resp.RowsAffected);

        using SqliteConnection check = _db.OpenConnection();
        Assert.Null(JiraIssueRecord.SelectList(check, Key: "FHIR-1").Single().ProcessedLocallyAt);
        Assert.Null(JiraProjectScopeStatementRecord.SelectList(check, Key: "PSS-1").Single().ProcessedLocallyAt);
        Assert.Null(JiraBaldefRecord.SelectList(check, Key: "BALDEF-1").Single().ProcessedLocallyAt);
        Assert.Null(JiraBallotRecord.SelectList(check, Key: "BALLOT-1").Single().ProcessedLocallyAt);
    }

    [Fact]
    public void SetProcessed_UnknownType_Returns400()
    {
        IActionResult result = _controller.SetProcessed(
            new JiraLocalProcessingSetRequest { Key = "X-1", ProcessedLocally = true }, type: "bogus");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void ClearAllProcessed_ClearsEveryRowAndReportsCount()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            SeedIssue(conn, "FHIR-1", processedAt: DateTimeOffset.UtcNow);
            SeedIssue(conn, "FHIR-2", processedAt: DateTimeOffset.UtcNow);
            SeedIssue(conn, "FHIR-3", processedAt: null);
        }

        JiraLocalProcessingClearResponse response = UnwrapClear(_controller.ClearAllProcessed());
        Assert.Equal(2, response.RowsAffected);

        using SqliteConnection conn2 = _db.OpenConnection();
        foreach (JiraIssueRecord r in JiraIssueRecord.SelectList(conn2))
        {
            Assert.Null(r.ProcessedLocallyAt);
        }
    }
}
