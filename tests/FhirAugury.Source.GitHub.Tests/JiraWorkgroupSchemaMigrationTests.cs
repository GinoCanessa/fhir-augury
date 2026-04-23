using FhirAugury.Source.GitHub.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Verifies the additive in-place migration on <c>jira_workgroups</c> introduced in
/// Phase 3: a legacy DB without the <c>WorkGroupCode</c> column / index gets both
/// after <see cref="GitHubDatabase.Initialize"/> runs.
/// </summary>
public class JiraWorkgroupSchemaMigrationTests : IDisposable
{
    private readonly string _tempDir;

    public JiraWorkgroupSchemaMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jira-wg-mig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        TestFileCleanup.SafeDeleteDirectory(_tempDir);
    }

    [Fact]
    public void Initialize_OnLegacySchema_AddsWorkGroupCodeColumnAndIndex()
    {
        string dbPath = Path.Combine(_tempDir, "legacy.db");

        // Pre-seed a legacy jira_workgroups schema without the WorkGroupCode column
        // or its index. Mirrors the column shape that existed prior to Phase 3.
        using (SqliteConnection seed = new($"Data Source={dbPath}"))
        {
            seed.Open();
            using SqliteCommand create = seed.CreateCommand();
            create.CommandText = """
                CREATE TABLE jira_workgroups (
                    Id INTEGER PRIMARY KEY,
                    RepoFullName TEXT NOT NULL,
                    WorkgroupKey TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    Webcode TEXT NULL,
                    Listserv TEXT NULL,
                    Deprecated INTEGER NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        // Sanity-check the legacy state.
        Assert.False(ColumnExists(dbPath, "jira_workgroups", "WorkGroupCode"));
        Assert.False(IndexExists(dbPath, "ix_jira_workgroups_WorkGroupCode"));

        // Run the GitHub database initializer over the legacy file.
        using (GitHubDatabase db = new(dbPath, NullLogger<GitHubDatabase>.Instance))
        {
            db.Initialize();
        }

        Assert.True(ColumnExists(dbPath, "jira_workgroups", "WorkGroupCode"));
        Assert.True(IndexExists(dbPath, "ix_jira_workgroups_WorkGroupCode"));
    }

    [Fact]
    public void Initialize_IsIdempotentOnRepeatedRuns()
    {
        string dbPath = Path.Combine(_tempDir, "fresh.db");

        using (GitHubDatabase db1 = new(dbPath, NullLogger<GitHubDatabase>.Instance))
        {
            db1.Initialize();
        }

        // Second initialize must not fail (column already exists, index already exists).
        using (GitHubDatabase db2 = new(dbPath, NullLogger<GitHubDatabase>.Instance))
        {
            db2.Initialize();
        }

        Assert.True(ColumnExists(dbPath, "jira_workgroups", "WorkGroupCode"));
        Assert.True(IndexExists(dbPath, "ix_jira_workgroups_WorkGroupCode"));
    }

    private static bool ColumnExists(string dbPath, string table, string column)
    {
        using SqliteConnection conn = new($"Data Source={dbPath}");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IndexExists(string dbPath, string indexName)
    {
        using SqliteConnection conn = new($"Data Source={dbPath}");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name=@n";
        cmd.Parameters.AddWithValue("@n", indexName);
        return cmd.ExecuteScalar() is not null;
    }
}
