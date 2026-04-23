using FhirAugury.Source.GitHub.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

/// <summary>
/// Phase 4 in-place schema migrations across <c>github_spec_file_map</c>,
/// <c>github_canonical_artifacts</c>, and <c>github_structure_definitions</c>.
/// Verifies that pre-Phase-4 tables (no Work-Group columns / indexes on
/// the file map; no <c>WorkGroupRaw</c> on the artifact tables) gain the
/// expected columns + indexes after <see cref="GitHubDatabase.Initialize"/>.
/// </summary>
public class WorkGroupSchemaMigrationTests : IDisposable
{
    private readonly string _tempDir;

    public WorkGroupSchemaMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wg-schema-mig-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    [Fact]
    public void Initialize_AddsWorkGroupColumnsAndIndexes_OnLegacySpecFileMap()
    {
        string dbPath = Path.Combine(_tempDir, "legacy.db");
        using (SqliteConnection seed = new($"Data Source={dbPath}"))
        {
            seed.Open();
            using SqliteCommand create = seed.CreateCommand();
            create.CommandText = """
                CREATE TABLE github_spec_file_map (
                    Id INTEGER PRIMARY KEY,
                    RepoFullName TEXT NOT NULL,
                    ArtifactKey TEXT NOT NULL,
                    FilePath TEXT NOT NULL,
                    MapType TEXT NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        Assert.False(ColumnExists(dbPath, "github_spec_file_map", "WorkGroup"));
        Assert.False(ColumnExists(dbPath, "github_spec_file_map", "WorkGroupRaw"));

        using (GitHubDatabase db = new(dbPath, NullLogger<GitHubDatabase>.Instance))
        {
            db.Initialize();
        }

        Assert.True(ColumnExists(dbPath, "github_spec_file_map", "WorkGroup"));
        Assert.True(ColumnExists(dbPath, "github_spec_file_map", "WorkGroupRaw"));
        Assert.True(IndexExists(dbPath, "ix_github_spec_file_map_RepoFullName_WorkGroup"));
        Assert.True(IndexExists(dbPath, "ix_github_spec_file_map_RepoFullName_WorkGroupRaw"));
    }

    [Fact]
    public void Initialize_AddsWorkGroupRawColumnAndIndex_OnLegacyCanonicalArtifacts()
    {
        string dbPath = Path.Combine(_tempDir, "legacy.db");
        using (SqliteConnection seed = new($"Data Source={dbPath}"))
        {
            seed.Open();
            using SqliteCommand create = seed.CreateCommand();
            create.CommandText = """
                CREATE TABLE github_canonical_artifacts (
                    Id INTEGER PRIMARY KEY,
                    RepoFullName TEXT NOT NULL,
                    FilePath TEXT NOT NULL,
                    ResourceType TEXT NOT NULL,
                    Url TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    Title TEXT NULL,
                    Version TEXT NULL,
                    Status TEXT NULL,
                    Description TEXT NULL,
                    Publisher TEXT NULL,
                    WorkGroup TEXT NULL,
                    FhirMaturity INTEGER NULL,
                    StandardsStatus TEXT NULL,
                    TypeSpecificData TEXT NULL,
                    Format TEXT NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        Assert.False(ColumnExists(dbPath, "github_canonical_artifacts", "WorkGroupRaw"));

        using (GitHubDatabase db = new(dbPath, NullLogger<GitHubDatabase>.Instance))
        {
            db.Initialize();
        }

        Assert.True(ColumnExists(dbPath, "github_canonical_artifacts", "WorkGroupRaw"));
        Assert.True(IndexExists(dbPath, "ix_github_canonical_artifacts_RepoFullName_WorkGroupRaw"));
    }

    [Fact]
    public void Initialize_AddsWorkGroupRawColumnAndIndex_OnLegacyStructureDefinitions()
    {
        string dbPath = Path.Combine(_tempDir, "legacy.db");
        using (SqliteConnection seed = new($"Data Source={dbPath}"))
        {
            seed.Open();
            using SqliteCommand create = seed.CreateCommand();
            create.CommandText = """
                CREATE TABLE github_structure_definitions (
                    Id INTEGER PRIMARY KEY,
                    RepoFullName TEXT NOT NULL,
                    FilePath TEXT NOT NULL,
                    Url TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    Title TEXT NULL,
                    Status TEXT NULL,
                    ArtifactClass TEXT NOT NULL,
                    Kind TEXT NOT NULL,
                    IsAbstract INTEGER NULL,
                    FhirType TEXT NULL,
                    BaseDefinition TEXT NULL,
                    Derivation TEXT NULL,
                    FhirVersion TEXT NULL,
                    Description TEXT NULL,
                    Publisher TEXT NULL,
                    WorkGroup TEXT NULL,
                    FhirMaturity INTEGER NULL,
                    StandardsStatus TEXT NULL,
                    Category TEXT NULL,
                    Contexts TEXT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        Assert.False(ColumnExists(dbPath, "github_structure_definitions", "WorkGroupRaw"));

        using (GitHubDatabase db = new(dbPath, NullLogger<GitHubDatabase>.Instance))
        {
            db.Initialize();
        }

        Assert.True(ColumnExists(dbPath, "github_structure_definitions", "WorkGroupRaw"));
        Assert.True(IndexExists(dbPath, "ix_github_structure_definitions_RepoFullName_WorkGroupRaw"));
    }

    [Fact]
    public void Initialize_IsIdempotent_OnFreshSchema()
    {
        string dbPath = Path.Combine(_tempDir, "fresh.db");
        using (GitHubDatabase db1 = new(dbPath, NullLogger<GitHubDatabase>.Instance)) { db1.Initialize(); }
        using (GitHubDatabase db2 = new(dbPath, NullLogger<GitHubDatabase>.Instance)) { db2.Initialize(); }

        Assert.True(ColumnExists(dbPath, "github_spec_file_map", "WorkGroup"));
        Assert.True(ColumnExists(dbPath, "github_spec_file_map", "WorkGroupRaw"));
        Assert.True(ColumnExists(dbPath, "github_canonical_artifacts", "WorkGroupRaw"));
        Assert.True(ColumnExists(dbPath, "github_structure_definitions", "WorkGroupRaw"));
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
