using FhirAugury.Database;
using FhirAugury.Database.Records;

namespace FhirAugury.Database.Tests;

public class DatabaseInitializationTests
{
    [Fact]
    public void InitializeDatabase_CreatesAllTables()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        // Verify tables exist by querying sqlite_master
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        var tables = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Contains("sync_state", tables);
        Assert.Contains("ingestion_log", tables);
        Assert.Contains("jira_issues", tables);
        Assert.Contains("jira_comments", tables);
    }

    [Fact]
    public void InitializeDatabase_CreatesFtsTables()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE '%_fts%' ORDER BY name;";
        var tables = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Contains("jira_issues_fts", tables);
        Assert.Contains("jira_comments_fts", tables);
    }

    [Fact]
    public void InitializeDatabase_CreatesTriggers()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' ORDER BY name;";
        var triggers = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            triggers.Add(reader.GetString(0));
        }

        Assert.Contains("jira_issues_ai", triggers);
        Assert.Contains("jira_issues_ad", triggers);
        Assert.Contains("jira_issues_au", triggers);
        Assert.Contains("jira_comments_ai", triggers);
        Assert.Contains("jira_comments_ad", triggers);
        Assert.Contains("jira_comments_au", triggers);
    }

    [Fact]
    public void InitializeDatabase_IsIdempotent()
    {
        using var conn = TestHelper.CreateInMemoryDb();

        // Call init again — should not throw
        SyncStateRecord.CreateTable(conn);
        IngestionLogRecord.CreateTable(conn);
        JiraIssueRecord.CreateTable(conn);
        JiraCommentRecord.CreateTable(conn);
        FtsSetup.CreateJiraFts(conn);
    }
}
