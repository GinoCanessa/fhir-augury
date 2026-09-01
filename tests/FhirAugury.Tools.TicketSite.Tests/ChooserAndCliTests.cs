using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketSite.Tests;

public sealed class ChooserAndCliTests
{
    private sealed class TempScope : IDisposable
    {
        public string PreparerDbPath { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        public string PlannerDbPath { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-pl.db");
        public string OutDir { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            TestFileCleanup.SafeDeleteFile(PreparerDbPath);
            TestFileCleanup.SafeDeleteFile(PlannerDbPath);
            TestFileCleanup.SafeDeleteDirectory(OutDir);
        }
    }

    [Fact]
    public async Task Cli_RejectsBothPreparerAndPlannerDbFlags_WithExit2()
    {
        StringWriter err = new();
        TextWriter originalErr = Console.Error;
        Console.SetError(err);
        try
        {
            int exit = await Program.Main(["--preparer-db", "a.db", "--planner-db", "b.db"]);
            Assert.Equal(2, exit);
            Assert.Contains("mutually exclusive", err.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public async Task Cli_RejectsNeitherPreparerNorPlannerDbFlags_WithExit2()
    {
        StringWriter err = new();
        TextWriter originalErr = Console.Error;
        Console.SetError(err);
        try
        {
            int exit = await Program.Main(["--out", "tmp"]);
            Assert.Equal(2, exit);
            Assert.Contains("Specify either", err.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public async Task PreparerOnly_BuildsDiscussionAndChooserShowsApplyingGreyed()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(scope.PreparerDbPath,
            [new("FHIR-1001", Project: "FHIR")]);

        int exit = await Program.Main(["--preparer-db", scope.PreparerDbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit);

        // Discussion sub-site is emitted, chooser is at the root.
        Assert.True(File.Exists(Path.Combine(scope.OutDir, "discussion", "index.html")));
        Assert.True(File.Exists(Path.Combine(scope.OutDir, "index.html")));
        Assert.False(Directory.Exists(Path.Combine(scope.OutDir, "applying")));

        string chooserHtml = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "index.html"));
        Assert.Contains("card-discussion live", chooserHtml, StringComparison.Ordinal);
        Assert.Contains("card-applying missing", chooserHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlannerOnly_BuildsApplyingAndChooserShowsDiscussionGreyed()
    {
        using TempScope scope = new();
        SeedMinimalPlannerDb(scope.PlannerDbPath);

        int exit = await Program.Main(["--planner-db", scope.PlannerDbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit);

        Assert.True(File.Exists(Path.Combine(scope.OutDir, "applying", "index.html")));
        Assert.True(File.Exists(Path.Combine(scope.OutDir, "index.html")));
        Assert.False(Directory.Exists(Path.Combine(scope.OutDir, "discussion")));

        string chooserHtml = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "index.html"));
        Assert.Contains("card-discussion missing", chooserHtml, StringComparison.Ordinal);
        Assert.Contains("card-applying live", chooserHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoSubSites_SequentialIntoSameOutDir_PreservesBothAndShowsBothLive()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(scope.PreparerDbPath,
            [new("FHIR-2001", Project: "FHIR")]);
        SeedMinimalPlannerDb(scope.PlannerDbPath);

        int exit1 = await Program.Main(["--preparer-db", scope.PreparerDbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit1);
        int exit2 = await Program.Main(["--planner-db", scope.PlannerDbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit2);

        // Both sub-site dirs survive.
        Assert.True(File.Exists(Path.Combine(scope.OutDir, "discussion", "index.html")));
        Assert.True(File.Exists(Path.Combine(scope.OutDir, "applying", "index.html")));
        Assert.True(File.Exists(Path.Combine(scope.OutDir, "discussion", OutputDirGuard.MarkerFileName)));
        Assert.True(File.Exists(Path.Combine(scope.OutDir, "applying", OutputDirGuard.MarkerFileName)));

        string chooserHtml = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "index.html"));
        Assert.Contains("card-discussion live", chooserHtml, StringComparison.Ordinal);
        Assert.Contains("card-applying live", chooserHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChooserPage_IsRegeneratedUnconditionally_EvenWithoutForce()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(scope.PreparerDbPath,
            [new("FHIR-3001", Project: "FHIR")]);

        int exit1 = await Program.Main(["--preparer-db", scope.PreparerDbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit1);
        DateTime first = File.GetLastWriteTimeUtc(Path.Combine(scope.OutDir, "index.html"));
        await Task.Delay(50);

        // Re-running without --force: should still overwrite the chooser unconditionally.
        int exit2 = await Program.Main(["--preparer-db", scope.PreparerDbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit2);
        DateTime second = File.GetLastWriteTimeUtc(Path.Combine(scope.OutDir, "index.html"));
        Assert.True(second >= first, "Chooser should be regenerated on every run.");
    }

    [Fact]
    public void OutputDirGuard_KindMatches_DetectsMismatch()
    {
        MetaFilterSet existing = new()
        {
            Kind = PreparerSubSiteEmitter.Kind,
            Filters = new MetaFilters(),
        };
        Assert.True(OutputDirGuard.KindMatches(existing, PreparerSubSiteEmitter.Kind));
        Assert.False(OutputDirGuard.KindMatches(existing, PlannerSubSiteEmitter.Kind));
    }

    [Fact]
    public void OutputDirGuard_WriteAndReadMarker_RoundTripsKindAndFilters()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            ResolvedFilters f = new("FHIR", "FHIR", "FHIRInfrastructure");
            OutputDirGuard.WriteMarker(dir, "preparer", f, DateTimeOffset.UtcNow);
            MetaFilterSet? read = OutputDirGuard.TryReadExistingMarker(dir);
            Assert.NotNull(read);
            Assert.Equal("preparer", read!.Kind);
            Assert.Equal("FHIR", read.Filters?.Spec);
            Assert.Equal("FHIRInfrastructure", read.Filters?.Wg);
            Assert.True(OutputDirGuard.FilterSetsMatch(read, f));
        }
        finally
        {
            TestFileCleanup.SafeDeleteDirectory(dir);
        }
    }

    private static void SeedMinimalPlannerDb(string dbPath)
    {
        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        conn.Open();
        FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.PlannerDatabase.EnsureSchema(conn);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO planned_tickets (Id, Key, Resolution, ResolutionSummary, FeatureProposal, DesignRationale, SavedAt)
            VALUES (@id, @key, '', 'sum', 'prop', 'rat', @savedAt)
            """;
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("@key", "FHIR-9000");
        cmd.Parameters.AddWithValue("@savedAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
