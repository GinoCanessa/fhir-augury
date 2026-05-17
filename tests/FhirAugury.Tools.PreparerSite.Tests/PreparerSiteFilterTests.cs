using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.PreparerSite.Tests;

public sealed class PreparerSiteFilterTests
{
    private sealed class TempScope : IDisposable
    {
        public string DbPath { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        public string OutDir { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best-effort */ }
            try { if (Directory.Exists(OutDir)) Directory.Delete(OutDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Args_ParseAllNewFlags_DoesNotAffectBaseline()
    {
        using TempScope scope = new();
        // Create a schema-less SQLite file so the run fails downstream, but the
        // parser still has to accept the new flags first.
        await using (SqliteConnection conn = new($"Data Source={scope.DbPath}"))
        {
            await conn.OpenAsync();
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE _ignore (x INTEGER)";
            await cmd.ExecuteNonQueryAsync();
        }

        StringWriter capturedErr = new();
        TextWriter originalErr = Console.Error;
        Console.SetError(capturedErr);
        try
        {
            await Program.Main(
            [
                "--db", scope.DbPath,
                "--out", scope.OutDir,
                "--spec", "X",
                "--project", "Y",
                "--wg", "Z",
                "--jira-source", "http://localhost:5160",
                "--jira-source-db", "/tmp/x.db",
                "--force",
            ]);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        string stderr = capturedErr.ToString();
        Assert.DoesNotContain("Unknown argument", stderr, StringComparison.Ordinal);
    }
}
