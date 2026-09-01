using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

/// <summary>
/// Exercises <see cref="ExtensionsCrossReferenceService"/> against a seeded
/// throwaway <c>github.db</c>: an extension with a core replacement is surfaced,
/// while an extension-only change with no core counterpart is suppressed.
/// </summary>
public sealed class ExtensionsCrossReferenceServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public ExtensionsCrossReferenceServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "extxref-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "github.db");
        SeedDb();
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    private void SeedDb()
    {
        using SqliteConnection conn = new($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using (SqliteCommand create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE github_structure_definitions (" +
                "Id INTEGER PRIMARY KEY, RepoFullName TEXT, Url TEXT, Name TEXT, Description TEXT)";
            create.ExecuteNonQuery();
        }
        Insert(conn, 1, "HL7/fhir-extensions", "http://example.org/ext/replaced", "PatientGenderExt",
            "This extension has been replaced by Patient.gender in the core specification.");
        Insert(conn, 2, "HL7/fhir-extensions", "http://example.org/ext/standalone", "StandaloneExt",
            "A standalone extension with no core equivalent.");
        // Wrong repo should never match.
        Insert(conn, 3, "HL7/fhir", "http://example.org/ext/replaced", "WrongRepo",
            "replaced by Patient.gender");
    }

    private static void Insert(SqliteConnection conn, int id, string repo, string url, string name, string description)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO github_structure_definitions (Id, RepoFullName, Url, Name, Description) " +
            "VALUES ($id, $repo, $url, $name, $desc)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$repo", repo);
        cmd.Parameters.AddWithValue("$url", url);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$desc", description);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Resolve_surfaces_extension_with_core_replacement()
    {
        IReadOnlyList<ExtensionCrossRef> refs = ExtensionsCrossReferenceService.Resolve(
            _dbPath, ["http://example.org/ext/replaced"]);

        ExtensionCrossRef crossRef = Assert.Single(refs);
        Assert.Equal("PatientGenderExt", crossRef.ExtensionName);
        Assert.Equal("Patient.gender", crossRef.ReplacementCoreElement);
        Assert.Contains("replaced by Patient.gender", crossRef.Rationale);
    }

    [Fact]
    public void Resolve_suppresses_extension_without_core_counterpart()
    {
        Assert.Empty(ExtensionsCrossReferenceService.Resolve(_dbPath, ["http://example.org/ext/standalone"]));
    }

    [Fact]
    public void Resolve_ignores_unknown_extension_and_other_repos()
    {
        Assert.Empty(ExtensionsCrossReferenceService.Resolve(_dbPath, ["http://example.org/ext/missing"]));
    }

    [Fact]
    public void Resolve_returns_empty_when_db_missing()
    {
        Assert.Empty(ExtensionsCrossReferenceService.Resolve(
            Path.Combine(_tempDir, "nope.db"), ["http://example.org/ext/replaced"]));
    }
}
