using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.TicketSite.Tests;

public sealed class PlannerSubSiteTests
{
    private sealed class TempScope : IDisposable
    {
        public string DbPath { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        public string OutDir { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            TestFileCleanup.SafeDeleteFile(DbPath);
            TestFileCleanup.SafeDeleteDirectory(OutDir);
        }
    }

    private static async Task SeedAsync(string dbPath, int planCount = 2, bool withTopic = false)
    {
        using PlannerDatabase database = new(dbPath, NullLogger<PlannerDatabase>.Instance);
        database.Initialize();
        for (int i = 1; i <= planCount; i++)
        {
            await database.SavePlannedTicketAsync(new PlannedTicketPayload
            {
                Key = $"FHIR-{1000 + i}",
                Resolution = "Persuasive",
                ResolutionSummary = $"summary {i}",
                FeatureProposal = $"proposal {i}",
                DesignRationale = $"rationale {i}",
                Repos = [new PlannedTicketRepoPayload { RepoKey = "HL7/fhir", Justification = "primary" }],
                RepoChanges = [
                    new PlannedTicketRepoChangePayload
                    {
                        TicketRepoId = "tr" + i,
                        RepoKey = "HL7/fhir",
                        ChangeSequence = 0,
                        FilePath = $"source/foo{i}.html",
                        ChangeTitle = $"add {i}",
                        ChangeDescription = "details",
                        ReplacementLines = ["line a"],
                        Reason = "spec change",
                    },
                ],
            });
            // Seed a self-Jira hydration row so the SPA's display JOINs return Title etc.
            HydrationBatch batch = new(
                TicketKey: $"FHIR-{1000 + i}",
                Parent: new HydrationTicketRow($"FHIR-{1000 + i}", null, null, null, "FHIR", null, null, null, null, null, null, null, DateTimeOffset.UtcNow, "resolved", null),
                JiraRows: [
                    new HydrationJiraRow($"FHIR-{1000 + i}", $"FHIR-{1000 + i}", $"Title {i}", "Triaged", "Change Request", null, null, null,
                        "FHIR Infrastructure", "FHIR", null, "https://x", DateTimeOffset.UtcNow, "resolved", null),
                ],
                ZulipRows: [], GitHubRows: [], RepoRows: [], JiraXrefRows: []);
            await ((IHydrationTargetDatabase)database).SaveHydrationAsync(batch, CancellationToken.None);
        }

        if (withTopic)
        {
            await database.SaveTopicGroupingAsync(new PlannedTicketTopicGroupingPayload
            {
                WorkGroupClean = "FHIRInfrastructure",
                WorkGroupDisplay = "FHIR Infrastructure",
                Specification = "FHIR",
                Type = "Change Request",
                Topics = [
                    new PlannedTicketTopicPayload
                    {
                        ShortDescription = "Coordinated change set",
                        LongerDescription = "Spans core + extensions.",
                        SpannedRepos = ["HL7/fhir", "HL7/fhir-extensions"],
                        RemainingTicketKeys = Enumerable.Range(1, planCount).Select(i => $"FHIR-{1000 + i}").ToList(),
                    },
                ],
            });
        }
    }

    [Fact]
    public async Task PlannerSubSite_Smoke_EmitsExpectedLandmarks()
    {
        using TempScope scope = new();
        await SeedAsync(scope.DbPath, planCount: 3);

        int exit = await Program.Main(["--planner-db", scope.DbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit);

        string indexPath = Path.Combine(scope.OutDir, "applying", "index.html");
        Assert.True(File.Exists(indexPath));
        string html = await File.ReadAllTextAsync(indexPath);
        Assert.Contains("Ticket Site", html, StringComparison.Ordinal);
        Assert.Contains("window.__DB__='", html, StringComparison.Ordinal);
        // Sub-site script tags present
        Assert.Contains("assets/sql-wasm.js", html, StringComparison.Ordinal);
        Assert.Contains("assets/app.js", html, StringComparison.Ordinal);
        // Vendored Markdown renderer + sanitizer referenced...
        Assert.Contains("assets/marked.min.js", html, StringComparison.Ordinal);
        Assert.Contains("assets/purify.min.js", html, StringComparison.Ordinal);
        // ...and loaded BEFORE app.js (otherwise md() silently falls back to escape).
        int markedIdx = html.IndexOf("assets/marked.min.js", StringComparison.Ordinal);
        int purifyIdx = html.IndexOf("assets/purify.min.js", StringComparison.Ordinal);
        int appIdx = html.IndexOf("assets/app.js", StringComparison.Ordinal);
        Assert.True(markedIdx >= 0 && markedIdx < appIdx);
        Assert.True(purifyIdx >= 0 && purifyIdx < appIdx);
        // Shared sql.js bytes emitted into the applying sub-site's assets/
        Assert.True(File.Exists(Path.Combine(scope.OutDir, "applying", "assets", "sql-wasm.js")));
        Assert.True(File.Exists(Path.Combine(scope.OutDir, "applying", "assets", "sql-wasm.wasm")));
        Assert.True(File.Exists(Path.Combine(scope.OutDir, "applying", "assets", "app.js")));
        // Vendored Markdown libs emitted into the applying sub-site's assets/
        Assert.True(File.Exists(Path.Combine(scope.OutDir, "applying", "assets", "marked.min.js")));
        Assert.True(File.Exists(Path.Combine(scope.OutDir, "applying", "assets", "purify.min.js")));
        // The emitted app.js wires the marked -> DOMPurify render path (no JS test harness).
        string appJs = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "applying", "assets", "app.js"));
        Assert.Contains("marked.parse(", appJs, StringComparison.Ordinal);
        Assert.Contains("DOMPurify.sanitize(", appJs, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(scope.OutDir, "applying", OutputDirGuard.MarkerFileName)));
    }

    [Fact]
    public async Task PlannerDbTrimmer_NoFilters_PreservesAllRows()
    {
        using TempScope scope = new();
        await SeedAsync(scope.DbPath, planCount: 2);

        PlannerDbTrimmer.BuildResult built = await PlannerDbTrimmer.BuildAsync(scope.DbPath, ResolvedFilters.None, CancellationToken.None);
        try
        {
            Assert.Equal(2, built.SurvivingTicketCount);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task PlannerDbTrimmer_BogusFilter_ProducesEmptyTrimmedDb()
    {
        using TempScope scope = new();
        await SeedAsync(scope.DbPath, planCount: 2);

        ResolvedFilters filter = new(Specification: "Bogus", Project: null, WorkGroup: null);
        PlannerDbTrimmer.BuildResult built = await PlannerDbTrimmer.BuildAsync(scope.DbPath, filter, CancellationToken.None);
        try
        {
            Assert.Equal(0, built.SurvivingTicketCount);

            // Confirm child tables also got trimmed.
            await using SqliteConnection conn = new($"Data Source={built.TempDbPath};Mode=ReadOnly;Pooling=False");
            await conn.OpenAsync();
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT (SELECT count(*) FROM planned_ticket_repos), (SELECT count(*) FROM planned_ticket_repo_changes)";
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal(0L, reader.GetInt64(1));
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task PlannerDbTrimmer_SelfMigratesLegacySchema()
    {
        // Create a "legacy" DB with only the core planned_tickets table; trim
        // should self-heal by calling PlannerDatabase.EnsureSchema and produce
        // a fully-tabled output.
        using TempScope scope = new();
        await using (SqliteConnection conn = new($"Data Source={scope.DbPath};Pooling=False"))
        {
            await conn.OpenAsync();
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE planned_tickets (RowId INTEGER PRIMARY KEY, Id TEXT, Key TEXT, Resolution TEXT, ResolutionSummary TEXT, FeatureProposal TEXT, DesignRationale TEXT, SavedAt TEXT)";
            await cmd.ExecuteNonQueryAsync();
        }
        TestFileCleanup.ClearSqlitePools();

        PlannerDbTrimmer.BuildResult built = await PlannerDbTrimmer.BuildAsync(scope.DbPath, ResolvedFilters.None, CancellationToken.None);
        try
        {
            await using SqliteConnection conn = new($"Data Source={built.TempDbPath};Mode=ReadOnly;Pooling=False");
            await conn.OpenAsync();
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='planned_ticket_topic_repos'";
            object? result = await cmd.ExecuteScalarAsync();
            Assert.NotNull(result); // EnsureSchema must have created the missing table
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task PlannerSubSite_WithTopics_InlinesTopicRows()
    {
        using TempScope scope = new();
        await SeedAsync(scope.DbPath, planCount: 2, withTopic: true);

        int exit = await Program.Main(["--planner-db", scope.DbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit);

        // Inspect the inlined DB to confirm planner_ticket_topic_repos has the two repos.
        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "applying", "index.html"));
        const string marker = "window.__DB__='";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += marker.Length;
        int end = html.IndexOf('\'', start);
        byte[] dbBytes = Convert.FromBase64String(html.Substring(start, end - start));
        string tempDbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await File.WriteAllBytesAsync(tempDbPath, dbBytes);
            await using SqliteConnection conn = new($"Data Source={tempDbPath};Mode=ReadOnly;Pooling=False");
            await conn.OpenAsync();
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM planned_ticket_topic_repos";
            object? result = await cmd.ExecuteScalarAsync();
            Assert.Equal(2L, (long)result!);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(tempDbPath);
        }
    }
}
