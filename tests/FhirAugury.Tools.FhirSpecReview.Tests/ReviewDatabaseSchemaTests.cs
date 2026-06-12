using FhirAugury.Tools.FhirSpecReview.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.FhirSpecReview.Tests;

/// <summary>
/// Verifies <see cref="ReviewDatabase"/> creates the full review schema
/// (all tables + composite UNIQUE indexes) and that <c>DropTables</c> removes
/// them. Raw connections use <c>;Pooling=False</c> and the temp dir is removed
/// via <see cref="TestFileCleanup"/>.
/// </summary>
public sealed class ReviewDatabaseSchemaTests : IDisposable
{
    private readonly string _tempDir;

    public ReviewDatabaseSchemaTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "review-schema-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    private static readonly string[] ExpectedTables =
    [
        "pages",
        "artifacts",
        "page_images",
        "page_unknown_words",
        "page_removed_fhir_artifacts",
        "removed_baseline_entities",
        "duplicate_artifact_keys",
        "workgroups",
        "review_runs",
    ];

    [Fact]
    public void Initialize_Creates_All_Tables_And_Unique_Indexes()
    {
        string dbPath = Path.Combine(_tempDir, "review.db");
        using (ReviewDatabase db = new(dbPath, NullLogger<ReviewDatabase>.Instance))
        {
            db.Initialize();
        }

        foreach (string table in ExpectedTables)
        {
            Assert.True(TableExists(dbPath, table), $"Expected table '{table}' to exist.");
        }

        Assert.True(IndexExists(dbPath, "ux_artifacts_repo_fhirid"));
        Assert.True(IndexExists(dbPath, "ux_pages_repo_file"));
        Assert.True(IndexExists(dbPath, "ux_removed_baseline_kind_name_release"));
    }

    [Fact]
    public void DropTables_Removes_All_Tables()
    {
        string dbPath = Path.Combine(_tempDir, "drop.db");
        using ReviewDatabase db = new(dbPath, NullLogger<ReviewDatabase>.Instance);
        db.Initialize();
        db.DropTables();

        foreach (string table in ExpectedTables)
        {
            Assert.False(TableExists(dbPath, table), $"Expected table '{table}' to be dropped.");
        }
    }

    private static bool TableExists(string dbPath, string table)
    {
        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool IndexExists(string dbPath, string indexName)
    {
        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name=$n";
        cmd.Parameters.AddWithValue("$n", indexName);
        return cmd.ExecuteScalar() is not null;
    }
}
