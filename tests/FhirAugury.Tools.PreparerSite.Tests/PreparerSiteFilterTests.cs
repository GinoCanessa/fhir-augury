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

    [Fact]
    public async Task Filter_UnknownSpec_ExitsNonZero_PrintsAvailableValues()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001"), new("FHIR-1002")],
            specByKey: new Dictionary<string, string?>
            {
                ["FHIR-1001"] = "FHIR",
                ["FHIR-1002"] = "CDS-Hooks",
            });

        (int exit, _, string stderr) = await RunMainAsync(
            "--db", scope.DbPath, "--out", scope.OutDir, "--spec", "Bogus");

        Assert.NotEqual(0, exit);
        Assert.Contains("Unknown value for --spec: 'Bogus'.", stderr, StringComparison.Ordinal);
        Assert.Contains("Available values:", stderr, StringComparison.Ordinal);
        Assert.Contains("CDS-Hooks", stderr, StringComparison.Ordinal);
        Assert.Contains("FHIR", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filter_UnknownProject_ExitsNonZero_PrintsAvailableValues()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [
                new("FHIR-1001", Project: "FHIR"),
                new("FHIR-1002", Project: "FHIR"),
                new("CDS-1", Project: "CDS"),
            ]);

        (int exit, _, string stderr) = await RunMainAsync(
            "--db", scope.DbPath, "--out", scope.OutDir, "--project", "Bogus");

        Assert.NotEqual(0, exit);
        Assert.Contains("Unknown value for --project: 'Bogus'.", stderr, StringComparison.Ordinal);
        Assert.Contains("Available values:", stderr, StringComparison.Ordinal);
        Assert.Contains("FHIR", stderr, StringComparison.Ordinal);
        Assert.Contains("CDS", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filter_UnknownWorkGroup_NoJiraSource_ExitsNonZero_PrintsHint()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", WorkGroup: "FHIR Infrastructure")]);

        // Use a guaranteed-unmatched workgroup token so that even if a real
        // Jira source service is running on localhost:5160, no real workgroup
        // can coincidentally match.
        string token = "fa-unknown-" + Guid.NewGuid().ToString("N");

        (int exit, _, string stderr) = await RunMainAsync(
            "--db", scope.DbPath, "--out", scope.OutDir, "--wg", token);

        Assert.NotEqual(0, exit);
        Assert.Contains($"Unknown value for --wg: '{token}'.", stderr, StringComparison.Ordinal);
        Assert.Contains(
            "To match by code, ensure the Jira source service is reachable or pass --jira-source-db <path>.",
            stderr,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filter_KnownValuesCaseInsensitive_AcceptedRegardlessOfCase()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", Project: "FHIR")]);

        (int exit, string stdout, _) = await RunMainAsync(
            "--db", scope.DbPath, "--out", scope.OutDir, "--project", "fhir");

        Assert.Equal(0, exit);
        Assert.Contains("Resolved --project 'fhir' → 'FHIR'.", stdout, StringComparison.Ordinal);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunMainAsync(params string[] args)
    {
        StringWriter capturedOut = new();
        StringWriter capturedErr = new();
        TextWriter originalOut = Console.Out;
        TextWriter originalErr = Console.Error;
        Console.SetOut(capturedOut);
        Console.SetError(capturedErr);
        int exit;
        try
        {
            exit = await Program.Main(args);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
        return (exit, capturedOut.ToString(), capturedErr.ToString());
    }
}
