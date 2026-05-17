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

    private static byte[] ExtractInlinedDbBytes(string html)
    {
        const string marker = "window.__DB__='";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "inlined DB marker not found");
        start += marker.Length;
        int end = html.IndexOf('\'', start);
        Assert.True(end > start, "inlined DB closing quote not found");
        return Convert.FromBase64String(html.Substring(start, end - start));
    }

    private static async Task<long> CountAsync(SqliteConnection conn, string table)
    {
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        object? value = await cmd.ExecuteScalarAsync();
        return value is long l ? l : Convert.ToInt64(value);
    }

    [Fact]
    public async Task Trim_SingleProject_ShrinksDb_AndDropsNonMatchingHydration()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [
                new("FHIR-1001", Project: "FHIR"),
                new("FHIR-1002", Project: "FHIR"),
                new("CDS-1", Project: "CDS"),
                new("CDS-2", Project: "CDS"),
            ],
            specByKey: new Dictionary<string, string?>
            {
                ["FHIR-1001"] = "FHIR",
                ["FHIR-1002"] = "FHIR",
                ["CDS-1"] = "CDS-Hooks",
                ["CDS-2"] = "CDS-Hooks",
            },
            seedAllChildTables: true);

        (int exit, _, _) = await RunMainAsync(
            "--db", scope.DbPath, "--out", scope.OutDir, "--project", "FHIR");
        Assert.Equal(0, exit);

        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "index.html"));
        byte[] dbBytes = ExtractInlinedDbBytes(html);
        string tempDb = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await File.WriteAllBytesAsync(tempDb, dbBytes);
            await using SqliteConnection conn = new($"Data Source={tempDb};Mode=ReadOnly");
            await conn.OpenAsync();

            Assert.Equal(2, await CountAsync(conn, "prepared_tickets"));
            Assert.Equal(2, await CountAsync(conn, "prepared_ticket_hydration"));
            Assert.Equal(2, await CountAsync(conn, "prepared_jira_hydration"));
            Assert.Equal(2, await CountAsync(conn, "prepared_github_hydration"));
            Assert.Equal(2, await CountAsync(conn, "prepared_repo_hydration"));
            Assert.Equal(2, await CountAsync(conn, "prepared_zulip_hydration"));
            Assert.Equal(2, await CountAsync(conn, "prepared_ticket_jira_xref"));
            Assert.Equal(2, await CountAsync(conn, "prepared_ticket_related_jira"));
            Assert.Equal(2, await CountAsync(conn, "prepared_ticket_repos"));
            Assert.Equal(2, await CountAsync(conn, "jira_processing_source_tickets"));
        }
        finally
        {
            try { File.Delete(tempDb); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Trim_AndCombination_AndsAllFilters()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [
                new("FHIR-1001", Project: "FHIR", WorkGroup: "FHIR Infrastructure"),
                new("FHIR-1002", Project: "FHIR", WorkGroup: "Clinical Decision Support"),
                new("CDS-1", Project: "CDS", WorkGroup: "Clinical Decision Support"),
            ],
            specByKey: new Dictionary<string, string?>
            {
                ["FHIR-1001"] = "FHIR",
                ["FHIR-1002"] = "FHIR",
                ["CDS-1"] = "CDS-Hooks",
            });

        (int exit, _, _) = await RunMainAsync(
            "--db", scope.DbPath, "--out", scope.OutDir,
            "--project", "FHIR",
            "--wg", "FHIR Infrastructure",
            "--spec", "FHIR");
        Assert.Equal(0, exit);

        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "index.html"));
        byte[] dbBytes = ExtractInlinedDbBytes(html);
        string tempDb = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await File.WriteAllBytesAsync(tempDb, dbBytes);
            await using SqliteConnection conn = new($"Data Source={tempDb};Mode=ReadOnly");
            await conn.OpenAsync();

            Assert.Equal(1, await CountAsync(conn, "prepared_tickets"));
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Key FROM prepared_tickets";
            object? key = await cmd.ExecuteScalarAsync();
            Assert.Equal("FHIR-1001", key);
        }
        finally
        {
            try { File.Delete(tempDb); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Trim_EmptyResult_ExitsZero_StillEmitsSite()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [
                new("FHIR-1001", Project: "FHIR", WorkGroup: "FHIR Infrastructure"),
                new("CDS-1", Project: "CDS", WorkGroup: "Clinical Decision Support"),
            ]);

        (int exit, string stdout, _) = await RunMainAsync(
            "--db", scope.DbPath, "--out", scope.OutDir,
            "--project", "FHIR", "--wg", "Clinical Decision Support");
        Assert.Equal(0, exit);
        Assert.Contains("0 prepared tickets match this filter.", stdout, StringComparison.Ordinal);

        string indexPath = Path.Combine(scope.OutDir, "index.html");
        Assert.True(File.Exists(indexPath));
        string html = await File.ReadAllTextAsync(indexPath);
        byte[] dbBytes = ExtractInlinedDbBytes(html);
        Assert.NotEmpty(dbBytes);

        string tempDb = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await File.WriteAllBytesAsync(tempDb, dbBytes);
            await using SqliteConnection conn = new($"Data Source={tempDb};Mode=ReadOnly");
            await conn.OpenAsync();
            Assert.Equal(0, await CountAsync(conn, "prepared_tickets"));
        }
        finally
        {
            try { File.Delete(tempDb); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Trim_DbBytesAreStrictlySmaller_ThanUnfiltered()
    {
        using TempScope unfilteredScope = new();
        using TempScope filteredScope = new();

        // Need enough payload bulk to span multiple SQLite pages so that
        // dropping half the tickets actually frees pages on VACUUM.
        List<PreparerTestDb.SourceTicketSeed> seeds = [];
        for (int i = 0; i < 60; i++)
        {
            seeds.Add(new($"FHIR-{2000 + i}", Project: "FHIR"));
        }
        for (int i = 0; i < 60; i++)
        {
            seeds.Add(new($"CDS-{i + 1}", Project: "CDS"));
        }

        await PreparerTestDb.SeedAsync(unfilteredScope.DbPath, seeds, seedAllChildTables: true);
        await PreparerTestDb.SeedAsync(filteredScope.DbPath, seeds, seedAllChildTables: true);

        (int unfilteredExit, _, _) = await RunMainAsync(
            "--db", unfilteredScope.DbPath, "--out", unfilteredScope.OutDir);
        Assert.Equal(0, unfilteredExit);
        (int filteredExit, _, _) = await RunMainAsync(
            "--db", filteredScope.DbPath, "--out", filteredScope.OutDir, "--project", "FHIR");
        Assert.Equal(0, filteredExit);

        byte[] unfilteredBytes = ExtractInlinedDbBytes(
            await File.ReadAllTextAsync(Path.Combine(unfilteredScope.OutDir, "index.html")));
        byte[] filteredBytes = ExtractInlinedDbBytes(
            await File.ReadAllTextAsync(Path.Combine(filteredScope.OutDir, "index.html")));

        Assert.True(filteredBytes.Length < unfilteredBytes.Length,
            $"Expected filtered bytes ({filteredBytes.Length}) < unfiltered bytes ({unfilteredBytes.Length}).");
    }
}
