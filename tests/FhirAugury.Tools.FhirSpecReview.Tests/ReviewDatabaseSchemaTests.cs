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
        "artifact_elements",
        "artifact_operations",
        "artifact_search_parameters",
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
    public void Initialize_Creates_FindingPointer_And_Snippet_Columns()
    {
        string dbPath = Path.Combine(_tempDir, "columns.db");
        using ReviewDatabase db = new(dbPath, NullLogger<ReviewDatabase>.Instance);
        db.Initialize();

        Assert.True(ColumnExists(dbPath, "pages", "SourceRelativePath"));
        Assert.True(ColumnExists(dbPath, "page_unknown_words", "ContextSnippet"));
        Assert.True(ColumnExists(dbPath, "page_removed_fhir_artifacts", "ContextSnippet"));
        Assert.True(ColumnExists(dbPath, "page_images", "ContextSnippet"));

        Assert.True(ColumnExists(dbPath, "artifact_elements", "ExternalRequiredBinding"));
        Assert.True(ColumnExists(dbPath, "artifact_operations", "OperationId"));
        Assert.True(ColumnExists(dbPath, "artifact_search_parameters", "SearchParamId"));

        Assert.Empty(db.FindMissingRequiredColumns());
    }

    [Fact]
    public void FindMissingRequiredColumns_Detects_Missing_Inventory_Table()
    {
        // Seed a DB that has every table EXCEPT artifact_elements, then confirm
        // the fast-fail guard reports the missing columns.
        string dbPath = Path.Combine(_tempDir, "legacy.db");
        using (ReviewDatabase db = new(dbPath, NullLogger<ReviewDatabase>.Instance))
        {
            db.Initialize();
        }

        using (SqliteConnection conn = new($"Data Source={dbPath};Pooling=False"))
        {
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE artifact_elements";
            cmd.ExecuteNonQuery();
        }

        using ReviewDatabase reopened = new(dbPath, NullLogger<ReviewDatabase>.Instance);
        List<(string Table, string Column)> missing = reopened.FindMissingRequiredColumns();
        Assert.NotEmpty(missing);
        Assert.Contains(missing, m => m.Table == "artifact_elements");
    }

    private static bool ColumnExists(string dbPath, string table, string column)
    {
        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
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
