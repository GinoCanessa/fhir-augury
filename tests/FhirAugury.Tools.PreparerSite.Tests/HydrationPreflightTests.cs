using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.PreparerSite.Tests;

[Collection("ConsoleRedirect")]
public sealed class HydrationPreflightTests
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

    private static async Task<long> CountHydrationRowsAsync(string dbPath)
    {
        await using SqliteConnection conn = new($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM prepared_ticket_hydration";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<bool> HasHydrationTableAsync(string dbPath)
    {
        await using SqliteConnection conn = new($"Data Source={dbPath};Mode=ReadOnly");
        await conn.OpenAsync();
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='prepared_ticket_hydration'";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunMainAsync(params string[] args)
    {
        StringWriter capturedErr = new();
        StringWriter capturedOut = new();
        TextWriter originalErr = Console.Error;
        TextWriter originalOut = Console.Out;
        Console.SetError(capturedErr);
        Console.SetOut(capturedOut);
        try
        {
            int exit = await Program.Main(args);
            return (exit, capturedOut.ToString(), capturedErr.ToString());
        }
        finally
        {
            Console.SetError(originalErr);
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task NoHydrate_OnLegacyDb_ExitsNonZero_WithActionableMessage()
    {
        using TempScope scope = new();
        await LegacyPreparerTestDb.SeedAsync(scope.DbPath,
        [
            new PreparerTestDb.SourceTicketSeed("FHIR-1001", Project: "FHIR"),
        ]);

        (int exit, _, string stderr) = await RunMainAsync(
            "--db", scope.DbPath, "--out", scope.OutDir, "--no-hydrate");

        Assert.NotEqual(0, exit);
        Assert.Contains("Hydration is missing", stderr, StringComparison.Ordinal);
        Assert.Contains(scope.DbPath, stderr, StringComparison.Ordinal);
        Assert.False(await HasHydrationTableAsync(scope.DbPath));
    }

    [Fact]
    public async Task AutoHydrate_OnLegacyDb_PopulatesHydration_AndEmitsSite()
    {
        using TempScope scope = new();
        using FakeHydrationServer server = new();
        await LegacyPreparerTestDb.SeedAsync(scope.DbPath,
        [
            new PreparerTestDb.SourceTicketSeed("FHIR-1001", Project: "FHIR"),
            new PreparerTestDb.SourceTicketSeed("FHIR-1002", Project: "FHIR"),
        ]);

        (int exit, _, string stderr) = await RunMainAsync(
            "--db", scope.DbPath, "--out", scope.OutDir, "--orchestrator", server.BaseUrl);

        Assert.Equal(0, exit);
        Assert.Contains("[info] Hydrating", stderr, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(scope.OutDir, "index.html")));
        // In-place mutation: the user's source DB now has hydration rows
        // (the fake server 404s, so rows land as 'unresolved' — but they land).
        long rows = await CountHydrationRowsAsync(scope.DbPath);
        Assert.Equal(2, rows);
    }

    [Fact]
    public async Task AutoHydrate_OnModernEmptyHydration_PopulatesRows()
    {
        using TempScope scope = new();
        using FakeHydrationServer server = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [
                new("FHIR-2001"),
                new("FHIR-2002"),
            ]);

        (int exit, _, string stderr) = await RunMainAsync(
            "--db", scope.DbPath, "--out", scope.OutDir, "--orchestrator", server.BaseUrl);

        Assert.Equal(0, exit);
        Assert.Contains("[info] Hydrating", stderr, StringComparison.Ordinal);
        long rows = await CountHydrationRowsAsync(scope.DbPath);
        Assert.True(rows > 0);
    }

    [Fact]
    public async Task AutoHydrate_OnHydratedDb_NoOp()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-3001")],
            specByKey: new Dictionary<string, string?>
            {
                ["FHIR-3001"] = "FHIR Core (FHIR)",
            });

        long before = await CountHydrationRowsAsync(scope.DbPath);

        (int exit, _, string stderr) = await RunMainAsync(
            "--db", scope.DbPath, "--out", scope.OutDir);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("[info] Hydrating", stderr, StringComparison.Ordinal);
        long after = await CountHydrationRowsAsync(scope.DbPath);
        Assert.Equal(before, after);
    }
}
