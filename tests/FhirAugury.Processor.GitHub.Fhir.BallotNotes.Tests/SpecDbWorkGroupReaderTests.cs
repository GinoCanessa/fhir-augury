using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

/// <summary>
/// Exercises <see cref="SpecDbWorkGroupReader"/> over seeded temp spec DBs:
/// <c>Structures.WorkGroup</c> lookup and the current-build (<c>fhir-r6.db</c>)
/// preference over the published (<c>fhir-spec.db</c>) DB.
/// </summary>
public sealed class SpecDbWorkGroupReaderTests : IDisposable
{
    private readonly string _tempDir;

    public SpecDbWorkGroupReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "specdbwg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    [Fact]
    public void Resolves_workgroup_for_structure_name()
    {
        string db = SeedStructures("r6.db", ("Patient", "pa"), ("Claim", "fm"));

        Assert.Equal("pa", SpecDbWorkGroupReader.Resolve(db, null, "Patient"));
        Assert.Equal("fm", SpecDbWorkGroupReader.Resolve(db, null, "claim"));
    }

    [Fact]
    public void Prefers_current_build_over_published()
    {
        string r6 = SeedStructures("fhir-r6.db", ("Patient", "pa-current"));
        string spec = SeedStructures("fhir-spec.db", ("Patient", "pa-published"));

        Assert.Equal("pa-current", SpecDbWorkGroupReader.Resolve(r6, spec, "Patient"));
    }

    [Fact]
    public void Falls_back_to_published_when_current_absent_or_missing_row()
    {
        string spec = SeedStructures("fhir-spec.db", ("Observation", "oo"));
        string missingR6 = Path.Combine(_tempDir, "nope.db");

        Assert.Equal("oo", SpecDbWorkGroupReader.Resolve(missingR6, spec, "Observation"));
    }

    [Fact]
    public void Returns_null_when_unresolved()
    {
        string db = SeedStructures("r6.db", ("Patient", "pa"));

        Assert.Null(SpecDbWorkGroupReader.Resolve(db, null, "Nonexistent"));
    }

    private string SeedStructures(string fileName, params (string Name, string WorkGroup)[] rows)
    {
        string path = Path.Combine(_tempDir, fileName);
        using SqliteConnection conn = new($"Data Source={path};Pooling=False");
        conn.Open();
        using (SqliteCommand create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE Structures (Id INTEGER PRIMARY KEY, Name TEXT, WorkGroup TEXT)";
            create.ExecuteNonQuery();
        }
        foreach ((string name, string wg) in rows)
        {
            using SqliteCommand ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO Structures (Name, WorkGroup) VALUES ($n, $w)";
            ins.Parameters.AddWithValue("$n", name);
            ins.Parameters.AddWithValue("$w", wg);
            ins.ExecuteNonQuery();
        }
        return path;
    }
}
