using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Jira.Tests;

public class JiraIndexBuilderWorkGroupsTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;
    private readonly JiraIndexBuilder _builder;

    public JiraIndexBuilderWorkGroupsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_idx_wg_{Guid.NewGuid():N}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
        _builder = new JiraIndexBuilder(NullLogger<JiraIndexBuilder>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static JiraIssueRecord NewIssue(string key, string? workGroup, string? status) => new JiraIssueRecord
    {
        Id = JiraIssueRecord.GetIndex(),
        Key = key,
        ProjectKey = "FHIR",
        Title = $"Issue {key}",
        Description = null,
        Summary = null,
        Type = "Bug",
        Priority = "Major",
        Status = status ?? "Open",
        Resolution = null,
        ResolutionDescription = null,
        Assignee = null,
        Reporter = null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        ResolvedAt = null,
        WorkGroup = workGroup,
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
        ProcessedLocallyAt = null,
    };

    private static Hl7WorkGroupRecord NewHl7Wg(string code, string name, string nameClean, bool retired = false) => new Hl7WorkGroupRecord
    {
        Id = Hl7WorkGroupRecord.GetIndex(),
        Code = code,
        Name = name,
        Definition = null,
        Retired = retired,
        NameClean = nameClean,
    };

    [Fact]
    public void Rebuild_PopulatesEveryBucketAndUnknownStatusFallsThrough()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            // Two HL7 work groups: one matched by Name, one only by NameClean.
            Hl7WorkGroupRecord.Insert(conn, NewHl7Wg("fhir", "FHIR Infrastructure", "FHIRInfrastructure"));
            Hl7WorkGroupRecord.Insert(conn, NewHl7Wg("oo", "Orders & Observations Canonical Name", "OrdersAndObservations"));

            // Issues: full coverage of mapped buckets for "FHIR Infrastructure".
            string[] mappedStatuses = [
                "Submitted", "Triaged", "Waiting for Input",
                "Resolved - No Change", "Resolved - Change Required",
                "Published", "Applied", "Duplicate", "Closed",
                "Balloted", "Withdrawn", "Deferred",
            ];
            int n = 0;
            foreach (string s in mappedStatuses)
            {
                JiraIssueRecord.Insert(conn, NewIssue($"FHIR-{++n}", "FHIR Infrastructure", s));
            }
            // Two unknown-status rows -> IssueCountOther on FHIR Infrastructure.
            JiraIssueRecord.Insert(conn, NewIssue($"FHIR-{++n}", "FHIR Infrastructure", "FooBar"));
            JiraIssueRecord.Insert(conn, NewIssue($"FHIR-{++n}", "FHIR Infrastructure", null));

            // NameClean-only match: issue's WorkGroup is "Orders & Observations"
            // which Cleans to "OrdersAndObservations" matching the seeded HL7 row.
            JiraIssueRecord.Insert(conn, NewIssue($"FHIR-{++n}", "Orders & Observations", "Submitted"));
            JiraIssueRecord.Insert(conn, NewIssue($"FHIR-{++n}", "Orders & Observations", "Submitted"));

            // Orphan workgroup: no HL7 match, by Name or NameClean.
            JiraIssueRecord.Insert(conn, NewIssue($"FHIR-{++n}", "Totally Unknown WG", "Triaged"));

            // Null/empty WorkGroup rows must be excluded entirely.
            JiraIssueRecord.Insert(conn, NewIssue($"FHIR-{++n}", null, "Submitted"));
            JiraIssueRecord.Insert(conn, NewIssue($"FHIR-{++n}", "", "Submitted"));

            _builder.RebuildIndexTables(conn);
        }

        using SqliteConnection check = _db.OpenConnection();
        List<JiraIndexWorkGroupRecord> rows = JiraIndexWorkGroupRecord.SelectList(check);

        Assert.Equal(3, rows.Count);

        JiraIndexWorkGroupRecord fhir = rows.Single(r => r.Name == "FHIR Infrastructure");
        Assert.NotNull(fhir.WorkGroupId);
        Assert.Equal(1, fhir.IssueCountSubmitted);
        Assert.Equal(1, fhir.IssueCountTriaged);
        Assert.Equal(1, fhir.IssueCountWaitingForInput);
        Assert.Equal(1, fhir.IssueCountNoChange);
        Assert.Equal(1, fhir.IssueCountChangeRequired);
        Assert.Equal(1, fhir.IssueCountPublished);
        Assert.Equal(1, fhir.IssueCountApplied);
        Assert.Equal(1, fhir.IssueCountDuplicate);
        Assert.Equal(1, fhir.IssueCountClosed);
        Assert.Equal(1, fhir.IssueCountBalloted);
        Assert.Equal(1, fhir.IssueCountWithdrawn);
        Assert.Equal(1, fhir.IssueCountDeferred);
        Assert.Equal(2, fhir.IssueCountOther); // "FooBar" + null
        Assert.Equal(14, fhir.IssueCount);
        Assert.Equal(SumBuckets(fhir), fhir.IssueCount);

        JiraIndexWorkGroupRecord oo = rows.Single(r => r.Name == "Orders & Observations");
        Assert.NotNull(oo.WorkGroupId);
        Assert.Equal(2, oo.IssueCountSubmitted);
        Assert.Equal(2, oo.IssueCount);
        Assert.Equal(SumBuckets(oo), oo.IssueCount);

        JiraIndexWorkGroupRecord orphan = rows.Single(r => r.Name == "Totally Unknown WG");
        Assert.Null(orphan.WorkGroupId);
        Assert.Equal(1, orphan.IssueCountTriaged);
        Assert.Equal(1, orphan.IssueCount);
    }

    [Fact]
    public void Rebuild_IsIdempotent_ReplacesPreviousRows()
    {
        using (SqliteConnection conn = _db.OpenConnection())
        {
            JiraIssueRecord.Insert(conn, NewIssue("FHIR-1", "Patient Care", "Submitted"));
            _builder.RebuildIndexTables(conn);
            _builder.RebuildIndexTables(conn);
        }

        using SqliteConnection check = _db.OpenConnection();
        List<JiraIndexWorkGroupRecord> rows = JiraIndexWorkGroupRecord.SelectList(check);
        Assert.Single(rows);
        Assert.Equal("Patient Care", rows[0].Name);
        Assert.Equal(1, rows[0].IssueCount);
        Assert.Equal(1, rows[0].IssueCountSubmitted);
    }

    private static int SumBuckets(JiraIndexWorkGroupRecord r) =>
        r.IssueCountSubmitted + r.IssueCountTriaged + r.IssueCountWaitingForInput
        + r.IssueCountNoChange + r.IssueCountChangeRequired
        + r.IssueCountPublished + r.IssueCountApplied + r.IssueCountDuplicate
        + r.IssueCountClosed + r.IssueCountBalloted + r.IssueCountWithdrawn
        + r.IssueCountDeferred + r.IssueCountOther;
}
