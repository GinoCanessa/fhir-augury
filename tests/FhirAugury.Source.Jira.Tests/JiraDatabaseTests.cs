using FhirAugury.Common.Api;
using FhirAugury.Common.Database;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.Jira.Tests;

public class JiraDatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly JiraDatabase _db;

    public JiraDatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"jira_test_{Guid.NewGuid()}.db");
        _db = new JiraDatabase(_dbPath, NullLogger<JiraDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Initialize_CreatesAllTables()
    {
        using SqliteConnection conn = _db.OpenConnection();
        List<string> tables = GetTableNames(conn);

        Assert.Contains("jira_issues", tables);
        Assert.Contains("jira_comments", tables);
        Assert.Contains("jira_issue_links", tables);
        Assert.Contains("sync_state", tables);
        Assert.Contains("index_keywords", tables);
        Assert.Contains("index_corpus", tables);
        Assert.Contains("index_doc_stats", tables);
        Assert.Contains("jira_users", tables);
        Assert.Contains("jira_issue_inpersons", tables);
        Assert.Contains("jira_index_users", tables);
        Assert.Contains("jira_index_inpersons", tables);
        Assert.Contains("hl7_workgroups", tables);
    }

    [Fact]
    public void Initialize_CreatesHl7WorkGroupsTable_WithExpectedColumns()
    {
        using SqliteConnection conn = _db.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(hl7_workgroups)";
        using SqliteDataReader reader = cmd.ExecuteReader();

        Dictionary<string, string> columns = new(StringComparer.Ordinal);
        while (reader.Read())
        {
            string name = reader.GetString(1);
            string type = reader.GetString(2);
            columns[name] = type;
        }

        Assert.Equal("INTEGER", columns["Id"]);
        Assert.Equal("TEXT", columns["Code"]);
        Assert.Equal("TEXT", columns["Name"]);
        Assert.Equal("TEXT", columns["Definition"]);
        Assert.Equal("INTEGER", columns["Retired"]);
        Assert.Equal("TEXT", columns["NameClean"]);
        Assert.Equal(6, columns.Count);
    }

    [Fact]
    public void Initialize_CreatesFtsVirtualTables()
    {
        using SqliteConnection conn = _db.OpenConnection();
        List<string> tables = GetTableNames(conn);

        Assert.Contains("jira_issues_fts", tables);
        Assert.Contains("jira_comments_fts", tables);
    }

    [Fact]
    public void MigrateSchema_AddsFR03Columns_OnLegacyJiraIndexWorkgroupsTable()
    {
        // Simulate a pre-FR-03 deployment: drop the freshly-created table
        // and recreate it with only the original three columns, then trigger
        // the migration path by re-initializing the schema.
        using (SqliteConnection conn = _db.OpenConnection())
        {
            using SqliteCommand drop = conn.CreateCommand();
            drop.CommandText = """
                DROP TABLE IF EXISTS jira_index_workgroups;
                CREATE TABLE jira_index_workgroups (
                  Id INTEGER PRIMARY KEY,
                  Name TEXT UNIQUE,
                  IssueCount INTEGER NOT NULL
                );
                """;
            drop.ExecuteNonQuery();
        }

        // Re-open and trigger schema initialize/migrate again.
        _db.Initialize();

        using SqliteConnection check = _db.OpenConnection();
        using SqliteCommand info = check.CreateCommand();
        info.CommandText = "PRAGMA table_info(jira_index_workgroups)";
        HashSet<string> cols = new(StringComparer.OrdinalIgnoreCase);
        using (SqliteDataReader r = info.ExecuteReader())
            while (r.Read()) cols.Add(r.GetString(1));

        Assert.Contains("WorkGroupId", cols);
        Assert.Contains("IssueCountSubmitted", cols);
        Assert.Contains("IssueCountTriaged", cols);
        Assert.Contains("IssueCountWaitingForInput", cols);
        Assert.Contains("IssueCountNoChange", cols);
        Assert.Contains("IssueCountChangeRequired", cols);
        Assert.Contains("IssueCountPublished", cols);
        Assert.Contains("IssueCountApplied", cols);
        Assert.Contains("IssueCountDuplicate", cols);
        Assert.Contains("IssueCountClosed", cols);
        Assert.Contains("IssueCountBalloted", cols);
        Assert.Contains("IssueCountWithdrawn", cols);
        Assert.Contains("IssueCountDeferred", cols);
        Assert.Contains("IssueCountOther", cols);

        // Idempotency: a second initialize must not blow up on already-added columns.
        _db.Initialize();
    }

    [Fact]
    public void InsertAndSelect_JiraIssue_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIssueRecord issue = CreateSampleIssue("FHIR-12345");

        JiraIssueRecord.Insert(conn, issue);
        JiraIssueRecord? result = JiraIssueRecord.SelectSingle(conn, Key: "FHIR-12345");

        Assert.NotNull(result);
        Assert.Equal("FHIR-12345", result.Key);
        Assert.Equal("Patient resource missing field", result.Title);
        Assert.Equal("Open", result.Status);
        Assert.Equal("FHIR-I", result.WorkGroup);
    }

    [Fact]
    public void SelectList_ByStatus_FiltersCorrectly()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIssueRecord.Insert(conn, CreateSampleIssue("FHIR-1", status: "Open"));
        JiraIssueRecord.Insert(conn, CreateSampleIssue("FHIR-2", status: "Closed"));
        JiraIssueRecord.Insert(conn, CreateSampleIssue("FHIR-3", status: "Open"));

        List<JiraIssueRecord> openIssues = JiraIssueRecord.SelectList(conn, Status: "Open");

        Assert.Equal(2, openIssues.Count);
        Assert.All(openIssues, i => Assert.Equal("Open", i.Status));
    }

    [Fact]
    public void Fts5_IndexesIssuesOnInsert()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIssueRecord.Insert(conn, CreateSampleIssue("FHIR-100", title: "Patient resource validation", labels: "resource"));
        JiraIssueRecord.Insert(conn, CreateSampleIssue("FHIR-101", title: "Observation code system", labels: "coding"));

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Key FROM jira_issues WHERE Id IN (SELECT rowid FROM jira_issues_fts WHERE jira_issues_fts MATCH '\"validation\"')";
        using SqliteDataReader reader = cmd.ExecuteReader();

        List<string> keys = new List<string>();
        while (reader.Read()) keys.Add(reader.GetString(0));

        Assert.Single(keys);
        Assert.Equal("FHIR-100", keys[0]);
    }

    [Fact]
    public void InsertAndSelect_JiraComment_RoundTrips()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIssueRecord.Insert(conn, CreateSampleIssue("FHIR-500"));

        JiraCommentRecord comment = new JiraCommentRecord
        {
            Id = JiraCommentRecord.GetIndex(),
            IssueId = 1,
            IssueKey = "FHIR-500",
            Author = "testuser",
            CreatedAt = DateTimeOffset.UtcNow,
            Body = "This is a test comment",
            BodyPlain = "This is a test comment",
        };
        JiraCommentRecord.Insert(conn, comment);

        List<JiraCommentRecord> result = JiraCommentRecord.SelectList(conn, IssueKey: "FHIR-500");
        Assert.Single(result);
        Assert.Equal("This is a test comment", result[0].Body);
    }

    [Fact]
    public void ResetDatabase_ClearsAndRecreates()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIssueRecord.Insert(conn, CreateSampleIssue("FHIR-999"));
        conn.Close();

        _db.ResetDatabase();

        using SqliteConnection conn2 = _db.OpenConnection();
        List<JiraIssueRecord> issues = JiraIssueRecord.SelectList(conn2);
        Assert.Empty(issues);

        // Tables still exist
        List<string> tables = GetTableNames(conn2);
        Assert.Contains("jira_issues", tables);
        Assert.Contains("jira_issues_fts", tables);
    }

    [Fact]
    public void CheckIntegrity_ReturnsOk()
    {
        string result = _db.CheckIntegrity();
        Assert.Equal("ok", result);
    }

    private static JiraIssueRecord CreateSampleIssue(
        string key,
        string title = "Patient resource missing field",
        string status = "Open",
        string workGroup = "FHIR-I",
        string labels = "bug,patient") => new()
    {
        Id = JiraIssueRecord.GetIndex(),
        Key = key,
        ProjectKey = "FHIR",
        Title = title,
        Description = "Sample description for testing",
        Summary = title,
        Type = "Bug",
        Priority = "Major",
        Status = status,
        Resolution = null,
        ResolutionDescription = null,
        Assignee = "testuser",
        Reporter = "reporter",
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
        UpdatedAt = DateTimeOffset.UtcNow,
        ResolvedAt = null,
        WorkGroup = workGroup,
        Specification = "FHIR Core",
        RaisedInVersion = "R4",
        SelectedBallot = null,
        RelatedArtifacts = null,
        RelatedIssues = null,
        DuplicateOf = null,
        AppliedVersions = null,
        ChangeType = null,
        Impact = null,
        Vote = null,
        Labels = labels,
        CommentCount = 0,
        ChangeCategory = null,
        ChangeImpact = null,
    };

    // ── Index list endpoint tests ─────────────────────────────────────

    [Fact]
    public void ListWorkGroups_ReturnsRecords_OrderedByCount()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIndexWorkGroupRecord.CreateTable(conn);
        JiraIndexWorkGroupRecord.Insert(conn, NewWg("Orders", 100));
        JiraIndexWorkGroupRecord.Insert(conn, NewWg("FHIR Infrastructure", 500));
        JiraIndexWorkGroupRecord.Insert(conn, NewWg("Patient Care", 200));

        List<JiraIndexWorkGroupRecord> records = JiraIndexWorkGroupRecord.SelectList(conn);

        Assert.Equal(3, records.Count);
        Assert.Contains(records, r => r.Name == "FHIR Infrastructure" && r.IssueCount == 500);
        Assert.Contains(records, r => r.Name == "Orders" && r.IssueCount == 100);
    }

    private static JiraIndexWorkGroupRecord NewWg(string name, int count) => new JiraIndexWorkGroupRecord
    {
        Id = JiraIndexWorkGroupRecord.GetIndex(),
        Name = name,
        IssueCount = count,
        IssueCountSubmitted = 0,
        IssueCountTriaged = 0,
        IssueCountWaitingForInput = 0,
        IssueCountNoChange = 0,
        IssueCountChangeRequired = 0,
        IssueCountPublished = 0,
        IssueCountApplied = 0,
        IssueCountDuplicate = 0,
        IssueCountClosed = 0,
        IssueCountBalloted = 0,
        IssueCountWithdrawn = 0,
        IssueCountDeferred = 0,
        IssueCountOther = 0,
    };

    [Fact]
    public void ListSpecifications_ReturnsRecords_OrderedByCount()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIndexSpecificationRecord.CreateTable(conn);
        JiraIndexSpecificationRecord.Insert(conn, new JiraIndexSpecificationRecord
        {
            Id = JiraIndexSpecificationRecord.GetIndex(), Name = "FHIR Core", IssueCount = 300,
        });
        JiraIndexSpecificationRecord.Insert(conn, new JiraIndexSpecificationRecord
        {
            Id = JiraIndexSpecificationRecord.GetIndex(), Name = "US Core", IssueCount = 150,
        });

        List<JiraIndexSpecificationRecord> records = JiraIndexSpecificationRecord.SelectList(conn);

        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.Name == "FHIR Core" && r.IssueCount == 300);
        Assert.Contains(records, r => r.Name == "US Core" && r.IssueCount == 150);
    }

    [Fact]
    public void ListStatuses_ReturnsRecords_OrderedByCount()
    {
        using SqliteConnection conn = _db.OpenConnection();
        JiraIndexStatusRecord.CreateTable(conn);
        JiraIndexStatusRecord.Insert(conn, new JiraIndexStatusRecord
        {
            Id = JiraIndexStatusRecord.GetIndex(), Name = "Open", IssueCount = 1000,
        });
        JiraIndexStatusRecord.Insert(conn, new JiraIndexStatusRecord
        {
            Id = JiraIndexStatusRecord.GetIndex(), Name = "Closed", IssueCount = 2000,
        });
        JiraIndexStatusRecord.Insert(conn, new JiraIndexStatusRecord
        {
            Id = JiraIndexStatusRecord.GetIndex(), Name = "In Progress", IssueCount = 50,
        });

        List<JiraIndexStatusRecord> records = JiraIndexStatusRecord.SelectList(conn);

        Assert.Equal(3, records.Count);
        Assert.Contains(records, r => r.Name == "Closed" && r.IssueCount == 2000);
        Assert.Contains(records, r => r.Name == "In Progress" && r.IssueCount == 50);
    }

    // ── Keyword query tests ────────────────────────────────────────────

    [Fact]
    public void GetKeywordsForItem_ReturnsMatchingKeywords()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('issue', 'test-item-1', 'patient', 5, 'word', 4.5),
                   ('issue', 'test-item-1', 'Patient.name', 3, 'fhir_path', 3.2),
                   ('issue', 'test-item-1', '$validate', 1, 'fhir_operation', 2.1),
                   ('issue', 'test-item-2', 'observation', 2, 'word', 1.5);
            """;
        insertCmd.ExecuteNonQuery();

        List<KeywordEntry> results = SourceDatabase.GetKeywordsForItem(connection, "test-item-1");
        Assert.Equal(3, results.Count);
        Assert.Equal("patient", results[0].Keyword);
        Assert.Equal(4.5, results[0].Bm25Score);
        Assert.Equal("Patient.name", results[1].Keyword);
        Assert.Equal("$validate", results[2].Keyword);
    }

    [Fact]
    public void GetKeywordsForItem_FiltersByKeywordType()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('issue', 'test-filter-1', 'patient', 5, 'word', 4.5),
                   ('issue', 'test-filter-1', 'Patient.name', 3, 'fhir_path', 3.2),
                   ('issue', 'test-filter-1', '$validate', 1, 'fhir_operation', 2.1);
            """;
        insertCmd.ExecuteNonQuery();

        List<KeywordEntry> results = SourceDatabase.GetKeywordsForItem(connection, "test-filter-1", keywordType: "fhir_path");
        Assert.Single(results);
        Assert.Equal("Patient.name", results[0].Keyword);
    }

    [Fact]
    public void GetKeywordsForItem_RespectsLimit()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('issue', 'test-limit-1', 'a', 1, 'word', 3.0),
                   ('issue', 'test-limit-1', 'b', 1, 'word', 2.0),
                   ('issue', 'test-limit-1', 'c', 1, 'word', 1.0);
            """;
        insertCmd.ExecuteNonQuery();

        List<KeywordEntry> results = SourceDatabase.GetKeywordsForItem(connection, "test-limit-1", limit: 2);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void GetKeywordsForItem_ReturnsEmpty_WhenNoMatch()
    {
        using SqliteConnection connection = _db.OpenConnection();
        List<KeywordEntry> results = SourceDatabase.GetKeywordsForItem(connection, "nonexistent-item");
        Assert.Empty(results);
    }

    [Fact]
    public void GetContentTypeForItem_ReturnsContentType()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('issue', 'test-ct-1', 'patient', 1, 'word', 1.0);
            """;
        insertCmd.ExecuteNonQuery();

        string contentType = SourceDatabase.GetContentTypeForItem(connection, "test-ct-1");
        Assert.Equal("issue", contentType);
    }

    [Fact]
    public void GetRelatedByKeyword_FindsRelatedItems()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('issue', 'seed-1', 'patient', 5, 'word', 4.0),
                   ('issue', 'seed-1', 'observation', 3, 'word', 3.0),
                   ('issue', 'related-1', 'patient', 4, 'word', 3.5),
                   ('issue', 'related-1', 'observation', 2, 'word', 2.0),
                   ('issue', 'related-2', 'patient', 1, 'word', 1.0),
                   ('issue', 'unrelated', 'medication', 5, 'word', 5.0);
            """;
        insertCmd.ExecuteNonQuery();

        var results = SourceDatabase.GetRelatedByKeyword(connection, "seed-1", minScore: 0.0);
        Assert.True(results.Count >= 1);
        Assert.Equal("related-1", results[0].SourceId);
        Assert.Contains("patient", results[0].SharedKeywords);
    }

    [Fact]
    public void GetRelatedByKeyword_ExcludesSeedItem()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('issue', 'self-1', 'patient', 5, 'word', 4.0),
                   ('issue', 'other-1', 'patient', 3, 'word', 3.0);
            """;
        insertCmd.ExecuteNonQuery();

        var results = SourceDatabase.GetRelatedByKeyword(connection, "self-1", minScore: 0.0);
        Assert.DoesNotContain(results, r => r.SourceId == "self-1");
    }

    [Fact]
    public void GetRelatedByKeyword_RespectsMinScore()
    {
        using SqliteConnection connection = _db.OpenConnection();
        using SqliteCommand insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO index_keywords (ContentType, SourceId, Keyword, Count, KeywordType, Bm25Score)
            VALUES ('issue', 'high-seed', 'patient', 5, 'word', 4.0),
                   ('issue', 'high-match', 'patient', 4, 'word', 3.5),
                   ('issue', 'low-match', 'patient', 1, 'word', 0.001);
            """;
        insertCmd.ExecuteNonQuery();

        var results = SourceDatabase.GetRelatedByKeyword(connection, "high-seed", minScore: 1.0);
        Assert.DoesNotContain(results, r => r.SourceId == "low-match");
    }

    private static List<string> GetTableNames(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'trigger') ORDER BY name";
        using SqliteDataReader reader = cmd.ExecuteReader();
        List<string> names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}
