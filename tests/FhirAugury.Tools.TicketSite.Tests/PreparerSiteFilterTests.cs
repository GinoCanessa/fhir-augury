using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.TicketSite.Tests;

[Collection("ConsoleRedirect")]
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
                "--preparer-db", scope.DbPath,
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
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--spec", "Bogus");

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
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--project", "Bogus");

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
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--wg", token);

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
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--project", "fhir");

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

    [Fact]
    public async Task Marker_FirstRun_WritesMetaFile_WithFilterSet()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", Project: "FHIR")]);

        (int exit, _, _) = await RunMainAsync(
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--project", "FHIR");
        Assert.Equal(0, exit);

        string markerPath = Path.Combine(scope.OutDir, "discussion", OutputDirGuard.MarkerFileName);
        Assert.True(File.Exists(markerPath));
        string json = await File.ReadAllTextAsync(markerPath);
        Assert.Contains("\"project\": \"FHIR\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"spec\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"wg\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Marker_UnfilteredRun_WritesEmptyFiltersObject()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", Project: "FHIR")]);

        (int exit, _, _) = await RunMainAsync("--preparer-db", scope.DbPath, "--out", scope.OutDir);
        Assert.Equal(0, exit);

        string markerPath = Path.Combine(scope.OutDir, "discussion", OutputDirGuard.MarkerFileName);
        Assert.True(File.Exists(markerPath));
        string json = await File.ReadAllTextAsync(markerPath);
        Assert.DoesNotContain("\"spec\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"project\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"wg\":", json, StringComparison.Ordinal);
        Assert.Contains("\"filters\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Marker_RerunSameFilterSet_OverwritesWithoutForce()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", Project: "FHIR")]);

        (int exit1, _, _) = await RunMainAsync(
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--project", "FHIR");
        Assert.Equal(0, exit1);

        (int exit2, _, _) = await RunMainAsync(
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--project", "FHIR");
        Assert.Equal(0, exit2);
    }

    [Fact]
    public async Task Marker_RerunDifferentFilterSet_RefusesWithoutForce_ExitsNonZero()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [
                new("FHIR-1001", Project: "FHIR"),
                new("CDS-1", Project: "CDS"),
            ]);

        (int exit1, _, _) = await RunMainAsync(
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--project", "FHIR");
        Assert.Equal(0, exit1);

        (int exit2, _, string stderr) = await RunMainAsync(
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--project", "CDS");
        Assert.NotEqual(0, exit2);
        Assert.Contains("was produced with a different filter set", stderr, StringComparison.Ordinal);
        Assert.Contains("--force", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Marker_RerunDifferentFilterSet_WithForce_Overwrites()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [
                new("FHIR-1001", Project: "FHIR"),
                new("CDS-1", Project: "CDS"),
            ]);

        (int exit1, _, _) = await RunMainAsync(
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--project", "FHIR");
        Assert.Equal(0, exit1);

        (int exit2, _, _) = await RunMainAsync(
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--project", "CDS", "--force");
        Assert.Equal(0, exit2);

        string markerPath = Path.Combine(scope.OutDir, "discussion", OutputDirGuard.MarkerFileName);
        string json = await File.ReadAllTextAsync(markerPath);
        Assert.Contains("\"project\": \"CDS\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Marker_PreExistingDirNoMarker_OverwritesWithoutForce()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", Project: "FHIR")]);

        Directory.CreateDirectory(scope.OutDir);
        Directory.CreateDirectory(Path.Combine(scope.OutDir, "discussion"));
        await File.WriteAllTextAsync(
            Path.Combine(scope.OutDir, "discussion", "index.html"),
            "<html>previously emitted, no marker</html>");

        (int exit, _, _) = await RunMainAsync(
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--project", "FHIR");
        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(scope.OutDir, "discussion", OutputDirGuard.MarkerFileName)));
    }

    [Fact]
    public async Task Render_Filtered_TitleHasSuffix()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", Project: "FHIR", WorkGroup: "FHIR Infrastructure")]);

        const string customTitle = "CDS — May 2026";
        (int exit, _, _) = await RunMainAsync(
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--title", customTitle,
            "--project", "FHIR", "--wg", "FHIR Infrastructure");
        Assert.Equal(0, exit);

        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "index.html"));
        Assert.Contains(
            $"<title>{customTitle} (filtered: project=FHIR, wg=FHIR Infrastructure)</title>",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            $"<h1>{customTitle} (filtered: project=FHIR, wg=FHIR Infrastructure)</h1>",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_Filtered_FiltersGlobalIsEmitted()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", Project: "FHIR")]);

        (int exit, _, _) = await RunMainAsync(
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--project", "FHIR");
        Assert.Equal(0, exit);

        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "index.html"));
        Assert.Contains("window.__FILTERS__={\"project\":\"FHIR\"};", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_Unfiltered_FiltersGlobalIsEmptyObject()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", Project: "FHIR")]);

        (int exit, _, _) = await RunMainAsync("--preparer-db", scope.DbPath, "--out", scope.OutDir);
        Assert.Equal(0, exit);

        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "index.html"));
        Assert.Contains("window.__FILTERS__={};", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_BundledAppJs_DropsRecommendationColumn_AddsArtifactPageColumns()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", Project: "FHIR")]);

        (int exit, _, _) = await RunMainAsync("--preparer-db", scope.DbPath, "--out", scope.OutDir);
        Assert.Equal(0, exit);

        string appJs = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "assets", "app.js"));
        // by-recommendation must be gone from the Crosscuts map and the
        // landing-grid order list.
        Assert.DoesNotContain("'by-recommendation'", appJs, StringComparison.Ordinal);
        // The two pruned facets must be gone end-to-end.
        Assert.DoesNotContain("'by-github-state'", appJs, StringComparison.Ordinal);
        Assert.DoesNotContain("'by-hydration-status'", appJs, StringComparison.Ordinal);
        // The two new filterable crosscut columns must be present.
        Assert.Contains("'by-artifact'", appJs, StringComparison.Ordinal);
        Assert.Contains("'by-page'", appJs, StringComparison.Ordinal);
        // Impact is now wired as a filter dimension.
        Assert.Contains("'impact'", appJs, StringComparison.Ordinal);
        // The new in-place chip-toggle wiring must be present.
        Assert.Contains("toggleChip", appJs, StringComparison.Ordinal);
        Assert.Contains("buildChipKeysSubquery", appJs, StringComparison.Ordinal);
        Assert.Contains("Show Ticket List", appJs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_BundledAppJs_PromotesImpactToChipDimension()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", Project: "FHIR")]);

        (int exit, _, _) = await RunMainAsync("--preparer-db", scope.DbPath, "--out", scope.OutDir);
        Assert.Equal(0, exit);

        string appJs = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "assets", "app.js"));
        // Pin the FilterableDimensions array literal exactly so a future
        // reorder or accidental drop of 'impact' is caught.
        Assert.Contains(
            "FilterableDimensions = ['spec', 'project', 'wg', 'type', 'artifact', 'page', 'impact']",
            appJs,
            StringComparison.Ordinal);
        // Pin the chip predicate switch case and its underlying impact
        // columns.
        Assert.Contains("case 'impact':", appJs, StringComparison.Ordinal);
        Assert.Contains("ProposalAImpact", appJs, StringComparison.Ordinal);
        Assert.Contains("ProposalBImpact", appJs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_BundledAppJs_PromotesTypeToChipDimension()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", Project: "FHIR")]);

        (int exit, _, _) = await RunMainAsync("--preparer-db", scope.DbPath, "--out", scope.OutDir);
        Assert.Equal(0, exit);

        string appJs = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "assets", "app.js"));
        // Pin the FilterableDimensions array literal exactly so a future
        // reorder or accidental drop of 'type' is caught.
        Assert.Contains(
            "FilterableDimensions = ['spec', 'project', 'wg', 'type', 'artifact', 'page', 'impact']",
            appJs,
            StringComparison.Ordinal);
        // Chip predicate branch must exist for 'type'.
        Assert.Contains("case 'type':", appJs, StringComparison.Ordinal);
        // Crosscut config entry for by-type must exist.
        Assert.Contains("'by-type': {", appJs, StringComparison.Ordinal);
        // Route handler branch must exist for by-type so deep-links and
        // the bare crosscut page both resolve.
        Assert.Contains("parts[0] === 'by-type'", appJs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_BundledAppJs_ListTableUsesTrimmedColumnSet()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", Project: "FHIR")]);

        (int exit, _, _) = await RunMainAsync("--preparer-db", scope.DbPath, "--out", scope.OutDir);
        Assert.Equal(0, exit);

        string appJs = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "assets", "app.js"));
        // Pin the seven-column set via the per-mount columns descriptor
        // (Phase 3 replaced the inline header array literal with a
        // descriptor list that the header loop and renderRows share).
        Assert.Contains("label: 'Key'", appJs, StringComparison.Ordinal);
        Assert.Contains("label: 'Title'", appJs, StringComparison.Ordinal);
        Assert.Contains("label: 'Workgroup'", appJs, StringComparison.Ordinal);
        Assert.Contains("label: 'Status'", appJs, StringComparison.Ordinal);
        Assert.Contains("label: 'Type'", appJs, StringComparison.Ordinal);
        Assert.Contains("label: 'Impact A'", appJs, StringComparison.Ordinal);
        Assert.Contains("label: 'Impact B'", appJs, StringComparison.Ordinal);
        // The old eight-column header literal must be gone.
        Assert.DoesNotContain(
            "'Recommendation', 'Impact', 'Saved'",
            appJs,
            StringComparison.Ordinal);
        // The combined-impact cell text must be gone.
        Assert.DoesNotContain(
            "'A: ' + String(r.ProposalAImpact",
            appJs,
            StringComparison.Ordinal);
        // Both proposal impacts must still be referenced individually.
        Assert.Contains("r.ProposalAImpact", appJs, StringComparison.Ordinal);
        Assert.Contains("r.ProposalBImpact", appJs, StringComparison.Ordinal);
        // SELECT list must be trimmed: no Recommendation, no SavedAt in
        // the list-view base SQL.
        Assert.DoesNotContain(
            "pt.Recommendation, pt.ProposalAImpact, pt.ProposalBImpact, pt.SavedAt",
            appJs,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_BundledAppJs_ListTableHeadersAreSortable()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", Project: "FHIR")]);

        (int exit, _, _) = await RunMainAsync("--preparer-db", scope.DbPath, "--out", scope.OutDir);
        Assert.Equal(0, exit);

        string appJs = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "assets", "app.js"));
        // aria-sort attribute powers the active sort affordance.
        Assert.Contains("aria-sort", appJs, StringComparison.Ordinal);
        // Key column uses natural-numeric compare so FHIR-5079 < FHIR-50710.
        Assert.Contains("numeric: true", appJs, StringComparison.Ordinal);
        // Sort state model uses sortCol / sortDir locals (renaming these
        // is a deliberate forcing-function for this iteration).
        Assert.Contains("sortCol", appJs, StringComparison.Ordinal);
        Assert.Contains("sortDir", appJs, StringComparison.Ordinal);
        // CSS hook for sortable headers.
        Assert.Contains("'sortable'", appJs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_BundledAppJs_UsesRepeatedKeyChipEncoding()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", Project: "FHIR")]);

        (int exit, _, _) = await RunMainAsync("--preparer-db", scope.DbPath, "--out", scope.OutDir);
        Assert.Equal(0, exit);

        string appJs = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "assets", "app.js"));
        // New encoder uses URLSearchParams.append per value; decoder uses
        // .getAll(dim). The old comma-joined form must not return.
        Assert.Contains("params.append(", appJs, StringComparison.Ordinal);
        Assert.Contains(".getAll(", appJs, StringComparison.Ordinal);
        Assert.DoesNotContain("values.join(',')", appJs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_BundledAppJs_ContainsUnifiedChipBanner()
    {
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-1001", Project: "FHIR")]);

        (int exit, _, _) = await RunMainAsync("--preparer-db", scope.DbPath, "--out", scope.OutDir);
        Assert.Equal(0, exit);

        // The emitted assets/app.js should ship the new unified chip
        // banner pipeline and no longer reference the removed
        // renderFilterFooter helper.
        string appJs = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "assets", "app.js"));
        Assert.Contains("renderChipBanner", appJs, StringComparison.Ordinal);
        Assert.Contains("FilterableDimensions", appJs, StringComparison.Ordinal);
        Assert.Contains("GenerationChips", appJs, StringComparison.Ordinal);
        Assert.DoesNotContain("renderFilterFooter", appJs, StringComparison.Ordinal);

        // The bundled index.html template should no longer ship the
        // server-rendered <div id="filter-banner"> placeholder — the
        // banner is created per-render by app.js now.
        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "index.html"));
        Assert.DoesNotContain("<div id=\"filter-banner\"></div>", html, StringComparison.Ordinal);
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
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--project", "FHIR");
        Assert.Equal(0, exit);

        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "index.html"));
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
            "--preparer-db", scope.DbPath, "--out", scope.OutDir,
            "--project", "FHIR",
            "--wg", "FHIR Infrastructure",
            "--spec", "FHIR");
        Assert.Equal(0, exit);

        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "index.html"));
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
            "--preparer-db", scope.DbPath, "--out", scope.OutDir,
            "--project", "FHIR", "--wg", "Clinical Decision Support");
        Assert.Equal(0, exit);
        Assert.Contains("0 prepared tickets match this filter.", stdout, StringComparison.Ordinal);

        string indexPath = Path.Combine(scope.OutDir, "discussion", "index.html");
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
            "--preparer-db", unfilteredScope.DbPath, "--out", unfilteredScope.OutDir);
        Assert.Equal(0, unfilteredExit);
        (int filteredExit, _, _) = await RunMainAsync(
            "--preparer-db", filteredScope.DbPath, "--out", filteredScope.OutDir, "--project", "FHIR");
        Assert.Equal(0, filteredExit);

        byte[] unfilteredBytes = ExtractInlinedDbBytes(
            await File.ReadAllTextAsync(Path.Combine(unfilteredScope.OutDir, "discussion", "index.html")));
        byte[] filteredBytes = ExtractInlinedDbBytes(
            await File.ReadAllTextAsync(Path.Combine(filteredScope.OutDir, "discussion", "index.html")));

        Assert.True(filteredBytes.Length < unfilteredBytes.Length,
            $"Expected filtered bytes ({filteredBytes.Length}) < unfiltered bytes ({unfilteredBytes.Length}).");
    }

    [Fact]
    public async Task FilterResolver_EmptyValues_PrintsHydrationHint()
    {
        // Seed a hydrated DB whose Specification column is null for every row,
        // so the SELECT DISTINCT in FilterResolver returns an empty list and
        // the new empty-`Available values:` replacement message fires. The
        // preflight sees a non-empty prepared_ticket_hydration table and
        // hands off to FilterResolver as a result.
        using TempScope scope = new();
        await PreparerTestDb.SeedAsync(
            scope.DbPath,
            [new("FHIR-9001")],
            specByKey: new Dictionary<string, string?>
            {
                ["FHIR-9001"] = null,
            });

        (int exit, _, string stderr) = await RunMainAsync(
            "--preparer-db", scope.DbPath, "--out", scope.OutDir, "--spec", "Bogus");

        Assert.NotEqual(0, exit);
        Assert.Contains("Unknown value for --spec: 'Bogus'.", stderr, StringComparison.Ordinal);
        Assert.Contains("No values are present for --spec in the database.", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Available values:", stderr, StringComparison.Ordinal);
    }
}
