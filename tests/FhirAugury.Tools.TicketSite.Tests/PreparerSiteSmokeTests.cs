using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Tools.TicketSite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.TicketSite.Tests;

[Collection("ConsoleRedirect")]
public sealed class PreparerSiteSmokeTests
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

    private static async Task SeedPreparerDbAsync(string dbPath, int ticketCount = 2)
    {
        using PreparerDatabase preparer = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        preparer.Initialize();

        for (int i = 1; i <= ticketCount; i++)
        {
            PreparedTicketPayload payload = new()
            {
                Key = $"FHIR-{1000 + i}",
                RequestSummary = $"Request summary body for ticket {i}.",
                CommentSummary = $"Comment summary body for ticket {i}.",
                ProposalA = $"Proposal A body for ticket {i}.",
                ProposalAJustification = $"Proposal A justification {i}.",
                ProposalAImpact = "Non-substantive",
                ProposalB = $"Proposal B body for ticket {i}.",
                ProposalBJustification = $"Proposal B justification {i}.",
                ProposalBImpact = "Compatible, substantive",
                ProposalC = $"Proposal C body for ticket {i}.",
                Recommendation = "A",
                RecommendationJustification = $"Recommendation justification {i}.",
            };
            payload.Repos.Add(new PreparedTicketRepoPayload
            {
                Repo = "HL7/fhir",
                RepoCategory = "core",
                Justification = $"Justification for ticket {i}.",
            });
            payload.RelatedJiraTickets.Add(new PreparedTicketRelatedJiraPayload
            {
                AssociatedTicketKey = $"FHIR-{2000 + i}",
                LinkType = "related",
                Justification = $"Related-jira justification {i}.",
            });
            await preparer.SavePreparedTicketAsync(payload);
        }

        // Always seed jira_processing_source_tickets so the SPA's LEFT JOIN
        // against it stays exercised by the smoke tests. Description is left
        // null to keep the per-test DB compact; we are no longer in the
        // business of stripping it.
        await using SqliteConnection connection = new($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync();
        for (int i = 1; i <= ticketCount; i++)
        {
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO jira_processing_source_tickets " +
                "(Id, Key, Title, Description, Project, Status, WorkGroup, Type, Specification, SourceTicketShape, LastSyncedAt, LastUpdated, ProcessingAttemptCount, ProcessingStatus) " +
                "VALUES (@id, @key, @title, @desc, @project, @status, @wg, @type, @spec, @shape, @synced, @updated, @pac, @ps)";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("@key", $"FHIR-{1000 + i}");
            cmd.Parameters.AddWithValue("@title", $"Source ticket title {i}");
            cmd.Parameters.AddWithValue("@desc", DBNull.Value);
            cmd.Parameters.AddWithValue("@project", "FHIR");
            cmd.Parameters.AddWithValue("@status", "Open");
            cmd.Parameters.AddWithValue("@wg", "FHIR Infrastructure");
            cmd.Parameters.AddWithValue("@type", "Change Request");
            cmd.Parameters.AddWithValue("@spec", "");
            cmd.Parameters.AddWithValue("@shape", "default");
            cmd.Parameters.AddWithValue("@synced", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@updated", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@pac", 0);
            cmd.Parameters.AddWithValue("@ps", "Done");
            await cmd.ExecuteNonQueryAsync();
        }

        // preparer-site requires a hydrated DB; seed a baseline hydration row
        // per ticket so smoke tests can exercise the emitter without re-seeding.
        string hydratedAt = DateTimeOffset.UtcNow.ToString("O");
        for (int i = 1; i <= ticketCount; i++)
        {
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO prepared_ticket_hydration " +
                "(Id, TicketKey, HydratedAt, HydrationStatus) " +
                "VALUES (@id, @key, @hat, 'resolved')";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("@key", $"FHIR-{1000 + i}");
            cmd.Parameters.AddWithValue("@hat", hydratedAt);
            await cmd.ExecuteNonQueryAsync();
        }
        // Force the WAL to merge back into the main DB so that the tool's raw
        // File.ReadAllBytes() sees everything just written.
        await using SqliteConnection cp = new($"Data Source={dbPath};Pooling=False");
        await cp.OpenAsync();
        await using SqliteCommand checkpoint = cp.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await checkpoint.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Emit_WithoutFilters_StillRoundTripsAllTickets()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath, ticketCount: 3);

        int exit = await Program.Main(["--preparer-db", scope.DbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit);

        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "index.html"));
        string base64 = ExtractInlinedDbBase64(html);
        byte[] dbBytes = Convert.FromBase64String(base64);

        string tempDbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await File.WriteAllBytesAsync(tempDbPath, dbBytes);
            await using SqliteConnection conn = new($"Data Source={tempDbPath};Mode=ReadOnly;Pooling=False");
            await conn.OpenAsync();
            Assert.Equal(3, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_tickets"));
            Assert.Equal(3, await ReadCountAsync(conn,
                "SELECT COUNT(*) FROM jira_processing_source_tickets"));
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(tempDbPath);
        }
    }

    [Fact]
    public async Task Emit_WritesIndexHtml_WithInlinedDb()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);

        int exit = await Program.Main(["--preparer-db", scope.DbPath, "--out", scope.OutDir]);

        Assert.Equal(0, exit);
        string indexPath = Path.Combine(scope.OutDir, "discussion", "index.html");
        Assert.True(File.Exists(indexPath));
        string html = await File.ReadAllTextAsync(indexPath);
        Assert.Contains("window.__DB__='", html, StringComparison.Ordinal);

        // The inlined DB is always a fresh build (trim + backfill + VACUUM),
        // so the byte size is independent of the source DB size and can
        // include extra schema (e.g., prepared_ticket_artifacts /
        // prepared_ticket_pages). Validate that the base64 round-trips into
        // a non-empty payload that's at least within a few SQLite-page
        // multiples of the source size, and that decoding succeeds.
        string base64 = ExtractInlinedDbBase64(html);
        byte[] dbBytes = Convert.FromBase64String(base64);

        long sourceSize = new FileInfo(scope.DbPath).Length;
        // Allow up to 64 KiB of extra schema overhead on top of the source size
        // and at least the source size (VACUUM can shrink, but not below the
        // surviving-row payload). The intent is to catch gross inlining bugs,
        // not to pin a precise byte count.
        Assert.InRange(dbBytes.Length, sourceSize - 64 * 1024, sourceSize + 64 * 1024);
    }

    [Fact]
    public async Task Emit_CopiesAssets()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);

        int exit = await Program.Main(["--preparer-db", scope.DbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit);

        string assetsDir = Path.Combine(scope.OutDir, "discussion", "assets");
        Assert.True(File.Exists(Path.Combine(assetsDir, "sql-wasm.js")));
        Assert.True(File.Exists(Path.Combine(assetsDir, "sql-wasm.wasm")));
        Assert.True(File.Exists(Path.Combine(assetsDir, "app.js")));
        Assert.True(File.Exists(Path.Combine(assetsDir, "app.css")));
    }

    [Fact]
    public async Task Emit_RespectsTitle_WhenProvided()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);

        const string customTitle = "CDS — May 2026";
        int exit = await Program.Main(["--preparer-db", scope.DbPath, "--out", scope.OutDir, "--title", customTitle]);
        Assert.Equal(0, exit);

        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "index.html"));
        Assert.Contains($"<title>{customTitle}</title>", html, StringComparison.Ordinal);
        Assert.Contains($"<h1>{customTitle}</h1>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<title>Ticket Site</title>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emit_UsesDefaultTitle_WhenOmitted()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);

        int exit = await Program.Main(["--preparer-db", scope.DbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit);

        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "index.html"));
        Assert.Contains("<title>Ticket Site</title>", html, StringComparison.Ordinal);
        Assert.Contains("<h1>Ticket Site</h1>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emit_HydratedDb_InlinesHydrationTables_RoundTrip()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);
        await SeedHydrationAsync(scope.DbPath);

        int exit = await Program.Main(["--preparer-db", scope.DbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit);

        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "discussion", "index.html"));
        string base64 = ExtractInlinedDbBase64(html);
        byte[] dbBytes = Convert.FromBase64String(base64);

        string tempDbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await File.WriteAllBytesAsync(tempDbPath, dbBytes);

            await using SqliteConnection conn = new($"Data Source={tempDbPath};Mode=ReadOnly;Pooling=False");
            await conn.OpenAsync();

            Assert.Equal(2, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_ticket_hydration"));
            Assert.Equal(2, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_jira_hydration"));
            Assert.Equal(2, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_zulip_hydration"));
            Assert.Equal(2, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_github_hydration"));
            Assert.Equal(2, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_repo_hydration"));
            Assert.Equal(1, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_ticket_jira_xref"));
            Assert.Equal(1, await ReadCountAsync(conn,
                "SELECT COUNT(*) FROM prepared_github_hydration WHERE HydrationStatus = 'unresolved'"));
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(tempDbPath);
        }
    }

    private static async Task<int> ReadCountAsync(SqliteConnection conn, string sql)
    {
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        object? value = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(value);
    }

    private static async Task SeedHydrationAsync(string dbPath)
    {
        await using SqliteConnection connection = new($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync();
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        string hydratedAtStr = hydratedAt.ToString("O");

        async Task RunAsync(string sql, (string, object)[] parameters)
        {
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            foreach ((string name, object value) in parameters) cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
        }

        // SeedPreparerDbAsync now seeds baseline hydration rows up front so
        // the hydration assertion passes for every smoke test; clear them
        // before inserting the richer per-ticket fixtures this test expects.
        await RunAsync("DELETE FROM prepared_ticket_hydration", []);

        for (int i = 1; i <= 2; i++)
        {
            string key = $"FHIR-{1000 + i}";
            await RunAsync(
                "INSERT INTO prepared_ticket_hydration (Id, TicketKey, Priority, Resolution, Specification, RaisedInVersion, CommentCount, DescriptionPlain, HydratedAt, HydrationStatus) " +
                "VALUES (@id, @key, 'Major', 'Persuasive', 'FHIR', '5.0.0', 3, 'plain body', @hat, 'resolved')",
                [("@id", Guid.NewGuid().ToString("N")), ("@key", key), ("@hat", hydratedAtStr)]);
            await RunAsync(
                "INSERT INTO prepared_jira_hydration (Id, TicketKey, JiraKey, Title, Status, Type, HydratedAt, HydrationStatus) " +
                "VALUES (@id, @key, @jk, 'Related ticket', 'Open', 'Change Request', @hat, 'resolved')",
                [("@id", Guid.NewGuid().ToString("N")), ("@key", key), ("@jk", $"FHIR-{2000 + i}"), ("@hat", hydratedAtStr)]);
            await RunAsync(
                "INSERT INTO prepared_zulip_hydration (Id, TicketKey, ZulipThreadId, StreamName, Topic, MessageCount, HydratedAt, HydrationStatus) " +
                "VALUES (@id, @key, @tid, 'implementers', 'ballot', 5, @hat, 'resolved')",
                [("@id", Guid.NewGuid().ToString("N")), ("@key", key), ("@tid", $"implementers:topic-{i}"), ("@hat", hydratedAtStr)]);
            // First github row resolved, second unresolved (1 unresolved total across the seed).
            string hydrationStatus = i == 1 ? "resolved" : "unresolved";
            string? reason = i == 1 ? null : "orchestrator 404";
            await RunAsync(
                "INSERT INTO prepared_github_hydration (Id, TicketKey, GitHubItemId, Owner, Repo, Number, State, IsPullRequest, HydratedAt, HydrationStatus, HydrationReason) " +
                "VALUES (@id, @key, @itm, 'HL7', 'fhir', @num, 'open', 0, @hat, @hs, @r)",
                [("@id", Guid.NewGuid().ToString("N")), ("@key", key), ("@itm", $"HL7/fhir#{i}"), ("@num", i), ("@hat", hydratedAtStr), ("@hs", hydrationStatus), ("@r", (object?)reason ?? DBNull.Value)]);
            await RunAsync(
                "INSERT INTO prepared_repo_hydration (Id, TicketKey, Repo, Description, CategoryDetail, Url, HydratedAt, HydrationStatus) " +
                "VALUES (@id, @key, 'HL7/fhir', 'core', 'FhirCore', 'https://github.com/HL7/fhir', @hat, 'resolved')",
                [("@id", Guid.NewGuid().ToString("N")), ("@key", key), ("@hat", hydratedAtStr)]);
        }

        await RunAsync(
            "INSERT INTO prepared_ticket_jira_xref (Id, TicketKey, JiraKey, Source) VALUES (@id, @key, @jk, @src)",
            [("@id", Guid.NewGuid().ToString("N")), ("@key", "FHIR-1001"), ("@jk", "FHIR-9999"), ("@src", "DuplicateOf")]);

        await using SqliteConnection cp = new($"Data Source={dbPath};Pooling=False");
        await cp.OpenAsync();
        await using SqliteCommand checkpoint = cp.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await checkpoint.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task EmitSite_OnUnhydratedDb_FailsFastWithActionableError()
    {
        // Replaces the previous LegacyPreparerTestDb test. preparer-site no
        // longer hydrates anything itself; if the DB has no
        // prepared_ticket_hydration rows the tool exits non-zero and points
        // the operator at the preparer service.
        using TempScope scope = new();
        // Seed only the source-ticket surface (no prepared_ticket_hydration).
        using PreparerDatabase preparer = new(scope.DbPath, NullLogger<PreparerDatabase>.Instance);
        preparer.Initialize();
        await using SqliteConnection cn = new($"Data Source={scope.DbPath};Pooling=False");
        await cn.OpenAsync();
        await using SqliteCommand insert = cn.CreateCommand();
        insert.CommandText =
            "INSERT INTO jira_processing_source_tickets " +
            "(Id, Key, Title, Description, Project, Status, WorkGroup, Type, Specification, SourceTicketShape, LastSyncedAt, LastUpdated, ProcessingAttemptCount, ProcessingStatus) " +
            "VALUES (@id, @key, 'T', NULL, 'FHIR', 'Open', 'WG', 'CR', '', 'default', @t, @t, 0, 'Done')";
        insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue("@key", "FHIR-1001");
        insert.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync();

        StringWriter capturedErr = new();
        StringWriter capturedOut = new();
        TextWriter originalErr = Console.Error;
        TextWriter originalOut = Console.Out;
        Console.SetError(capturedErr);
        Console.SetOut(capturedOut);
        int exit;
        try
        {
            exit = await Program.Main(["--preparer-db", scope.DbPath, "--out", scope.OutDir, "--project", "FHIR"]);
        }
        finally
        {
            Console.SetError(originalErr);
            Console.SetOut(originalOut);
        }

        string stderr = capturedErr.ToString();
        Assert.NotEqual(0, exit);
        Assert.Contains("not hydrated", stderr, StringComparison.Ordinal);
        Assert.Contains("FhirAugury.Processor.Jira.Fhir.Preparer", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(scope.OutDir, "discussion", "index.html")));
    }

    [Fact]
    public async Task Emit_FailsCleanly_WhenPruneFlagPassed()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);

        StringWriter capturedErr = new();
        TextWriter originalErr = Console.Error;
        Console.SetError(capturedErr);
        int exit;
        try
        {
            exit = await Program.Main(["--preparer-db", scope.DbPath, "--out", scope.OutDir, "--prune"]);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        Assert.NotEqual(0, exit);
        string stderr = capturedErr.ToString();
        Assert.Contains("--prune", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emit_FailsCleanly_WhenDbMissing()
    {
        using TempScope scope = new();
        string bogus = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");

        StringWriter capturedErr = new();
        TextWriter originalErr = Console.Error;
        Console.SetError(capturedErr);
        int exit;
        try
        {
            exit = await Program.Main(["--preparer-db", bogus, "--out", scope.OutDir]);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        Assert.NotEqual(0, exit);
        string stderr = capturedErr.ToString();
        Assert.Contains("not found", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Emit_FailsCleanly_WhenSchemaMissing()
    {
        using TempScope scope = new();
        // Create a valid but schema-less SQLite file: open, run a no-op, close.
        await using (SqliteConnection conn = new($"Data Source={scope.DbPath};Pooling=False"))
        {
            await conn.OpenAsync();
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE _ignore (x INTEGER)";
            await cmd.ExecuteNonQueryAsync();
        }

        StringWriter capturedErr = new();
        TextWriter originalErr = Console.Error;
        Console.SetError(capturedErr);
        int exit;
        try
        {
            exit = await Program.Main(["--preparer-db", scope.DbPath, "--out", scope.OutDir]);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        Assert.NotEqual(0, exit);
        string stderr = capturedErr.ToString();
        // The hydration assertion now fires first against a schema-less DB
        // (no prepared_ticket_hydration table) with an actionable message.
        Assert.Contains("not hydrated", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Emit_WithoutJiraSourceDb_CreatesEmptyArtifactTables()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);

        StringWriter capturedErr = new();
        TextWriter originalErr = Console.Error;
        Console.SetError(capturedErr);
        int exit;
        try
        {
            exit = await Program.Main(["--preparer-db", scope.DbPath, "--out", scope.OutDir]);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        Assert.Equal(0, exit);
        Assert.Contains("Related-artifact/page backfill skipped", capturedErr.ToString(), StringComparison.Ordinal);

        await using SqliteConnection conn = await OpenInlinedDbAsync(scope.OutDir);
        Assert.Equal(1, await ReadCountAsync(conn,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='prepared_ticket_artifacts'"));
        Assert.Equal(1, await ReadCountAsync(conn,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='prepared_ticket_pages'"));
        Assert.Equal(0, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_ticket_artifacts"));
        Assert.Equal(0, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_ticket_pages"));
    }

    [Fact]
    public async Task Emit_WithJiraSourceDb_PopulatesArtifactTables()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);

        string jiraSourcePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-src.db");
        try
        {
            await CreateFakeJiraSourceDbAsync(jiraSourcePath, new Dictionary<string, string?>
            {
                ["FHIR-1001"] = "Observation, Patient, Observation",
                ["FHIR-1002"] = " Encounter ,, Observation ",
            });

            int exit = await Program.Main([
                "--preparer-db", scope.DbPath, "--out", scope.OutDir,
                "--jira-source-db", jiraSourcePath,
            ]);
            Assert.Equal(0, exit);

            await using SqliteConnection conn = await OpenInlinedDbAsync(scope.OutDir);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT TicketKey, Value FROM prepared_ticket_artifacts " +
                "ORDER BY TicketKey, Value";
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
            List<(string Key, string Value)> rows = [];
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }
            // Per-ticket case-insensitive de-dup; "Observation" appears once
            // per ticket even though it was listed twice.
            Assert.Equal(
                new[]
                {
                    ("FHIR-1001", "Observation"),
                    ("FHIR-1001", "Patient"),
                    ("FHIR-1002", "Encounter"),
                    ("FHIR-1002", "Observation"),
                },
                rows);

            // No jira_baldef rows seeded, so pages table is empty.
            Assert.Equal(0, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_ticket_pages"));
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(jiraSourcePath);
        }
    }

    [Fact]
    public async Task InlinedDb_ArtifactCrosscut_GroupsAndCounts()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);

        string jiraSourcePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-src.db");
        try
        {
            // T1 (FHIR-1001): Observation, Patient.
            // T2 (FHIR-1002): Observation.
            await CreateFakeJiraSourceDbAsync(jiraSourcePath, new Dictionary<string, string?>
            {
                ["FHIR-1001"] = "Observation, Patient",
                ["FHIR-1002"] = "Observation",
            });

            int exit = await Program.Main([
                "--preparer-db", scope.DbPath, "--out", scope.OutDir,
                "--jira-source-db", jiraSourcePath,
            ]);
            Assert.Equal(0, exit);

            await using SqliteConnection conn = await OpenInlinedDbAsync(scope.OutDir);
            // Mirror the SPA's by-artifact crosscut SQL (no chip WHERE here).
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Value AS k, COUNT(DISTINCT TicketKey) AS n " +
                "FROM prepared_ticket_artifacts " +
                "WHERE TicketKey IN (SELECT Key FROM prepared_tickets) " +
                "GROUP BY Value ORDER BY n DESC, k";
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
            List<(string K, long N)> rows = [];
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.GetInt64(1)));
            }
            Assert.Equal(new[] { ("Observation", 2L), ("Patient", 1L) }, rows);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(jiraSourcePath);
        }
    }

    [Fact]
    public async Task InlinedDb_PageCrosscut_GroupsAndCounts()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);

        string jiraSourcePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-src.db");
        try
        {
            // jira_baldef rows seeded: FHIR-1001 → security; FHIR-1002 →
            // security, terminologies.
            await CreateFakeJiraSourceDbAsync(
                jiraSourcePath,
                artifactsByKey: new Dictionary<string, string?>(),
                baldefByKey: new Dictionary<string, (string?, string?)>
                {
                    ["FHIR-1001"] = (null, "security"),
                    ["FHIR-1002"] = (null, "security, terminologies"),
                });

            int exit = await Program.Main([
                "--preparer-db", scope.DbPath, "--out", scope.OutDir,
                "--jira-source-db", jiraSourcePath,
            ]);
            Assert.Equal(0, exit);

            await using SqliteConnection conn = await OpenInlinedDbAsync(scope.OutDir);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Value AS k, COUNT(DISTINCT TicketKey) AS n " +
                "FROM prepared_ticket_pages " +
                "WHERE TicketKey IN (SELECT Key FROM prepared_tickets) " +
                "GROUP BY Value ORDER BY n DESC, k";
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
            List<(string K, long N)> rows = [];
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.GetInt64(1)));
            }
            Assert.Equal(new[] { ("security", 2L), ("terminologies", 1L) }, rows);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(jiraSourcePath);
        }
    }

    private static async Task CreateFakeJiraSourceDbAsync(
        string dbPath,
        IReadOnlyDictionary<string, string?> artifactsByKey,
        IReadOnlyDictionary<string, (string? Artifacts, string? Pages)>? baldefByKey = null)
    {
        await using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        await conn.OpenAsync();
        await using (SqliteCommand cmd = conn.CreateCommand())
        {
            // Minimal schemas — only the columns the backfill reads.
            cmd.CommandText = """
                CREATE TABLE jira_issues (
                  Key TEXT PRIMARY KEY,
                  RelatedArtifacts TEXT
                );
                CREATE TABLE jira_baldef (
                  Key TEXT PRIMARY KEY,
                  RelatedArtifacts TEXT,
                  RelatedPages TEXT
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        foreach ((string key, string? raw) in artifactsByKey)
        {
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO jira_issues (Key, RelatedArtifacts) VALUES (@k, @v)";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@v", (object?)raw ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        if (baldefByKey is not null)
        {
            foreach ((string key, (string? artifacts, string? pages)) in baldefByKey)
            {
                await using SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO jira_baldef (Key, RelatedArtifacts, RelatedPages) " +
                    "VALUES (@k, @a, @p)";
                cmd.Parameters.AddWithValue("@k", key);
                cmd.Parameters.AddWithValue("@a", (object?)artifacts ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@p", (object?)pages ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task<SqliteConnection> OpenInlinedDbAsync(string outDir)
    {
        string html = await File.ReadAllTextAsync(Path.Combine(outDir, "discussion", "index.html"));
        string base64 = ExtractInlinedDbBase64(html);
        byte[] bytes = Convert.FromBase64String(base64);
        string tempDbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        await File.WriteAllBytesAsync(tempDbPath, bytes);
        SqliteConnection conn = new($"Data Source={tempDbPath};Mode=ReadOnly;Pooling=False");
        await conn.OpenAsync();
        return conn;
    }

    private static string ExtractInlinedDbBase64(string html)
    {
        const string marker = "window.__DB__='";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "inlined DB marker not found");
        start += marker.Length;
        int end = html.IndexOf('\'', start);
        Assert.True(end > start, "inlined DB closing quote not found");
        return html.Substring(start, end - start);
    }

    /// <summary>
    /// Seeds two topics with one group and three member rows. Assumes
    /// <see cref="SeedPreparerDbAsync"/> has already inserted tickets
    /// <c>FHIR-1001</c> and <c>FHIR-1002</c>.
    ///
    /// Topic 1 (RenderOrderHint=1) — "Observation status semantics":
    ///   - Group 1 (FirstTicketKey="FHIR-1001"): FHIR-1001 (OrderInContainer=0)
    ///   - Ungrouped: FHIR-1002 (OrderInContainer=1)
    /// Topic 2 (RenderOrderHint=NULL) — "Reference target widening":
    ///   - Ungrouped: FHIR-1002 (OrderInContainer=0)
    /// </summary>
    private static async Task SeedTopicsAsync(string dbPath)
    {
        await using SqliteConnection connection = new($"Data Source={dbPath};Pooling=False");
        await connection.OpenAsync();
        string savedAt = DateTimeOffset.UtcNow.ToString("O");

        async Task<int> InsertTopicAsync(string id, string shortDesc, string longDesc, int? hint)
        {
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO prepared_ticket_topics " +
                "(Id, WorkGroupClean, WorkGroupDisplay, Specification, Type, " +
                " ShortDescription, LongerDescription, RenderOrderHint, SavedAt) " +
                "VALUES (@id, @wgc, @wgd, @spec, @type, @sd, @ld, @hint, @sa); " +
                "SELECT last_insert_rowid()";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@wgc", "fhir-i");
            cmd.Parameters.AddWithValue("@wgd", "FHIR Infrastructure");
            cmd.Parameters.AddWithValue("@spec", "FHIR Core");
            cmd.Parameters.AddWithValue("@type", "Change Request");
            cmd.Parameters.AddWithValue("@sd", shortDesc);
            cmd.Parameters.AddWithValue("@ld", longDesc);
            cmd.Parameters.AddWithValue("@hint", (object?)hint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sa", savedAt);
            object? scalar = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(scalar);
        }

        async Task<int> InsertGroupAsync(int topicRowId, string firstTicketKey, string rationale, int orderInTopic)
        {
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO prepared_ticket_topic_groups " +
                "(Id, TopicRowId, FirstTicketKey, Rationale, OrderInTopic, SavedAt) " +
                "VALUES (@id, @trid, @ftk, @rat, @oit, @sa); " +
                "SELECT last_insert_rowid()";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("@trid", topicRowId);
            cmd.Parameters.AddWithValue("@ftk", firstTicketKey);
            cmd.Parameters.AddWithValue("@rat", rationale);
            cmd.Parameters.AddWithValue("@oit", orderInTopic);
            cmd.Parameters.AddWithValue("@sa", savedAt);
            object? scalar = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(scalar);
        }

        async Task InsertMemberAsync(int topicRowId, int? groupRowId, string ticketKey, int orderInContainer)
        {
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO prepared_ticket_topic_members " +
                "(Id, TopicRowId, TopicGroupRowId, TicketKey, OrderInContainer) " +
                "VALUES (@id, @trid, @grid, @tk, @oc)";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("@trid", topicRowId);
            cmd.Parameters.AddWithValue("@grid", (object?)groupRowId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tk", ticketKey);
            cmd.Parameters.AddWithValue("@oc", orderInContainer);
            await cmd.ExecuteNonQueryAsync();
        }

        int topic1Rid = await InsertTopicAsync(
            "00000000000000000000000000000001",
            "Observation status semantics",
            "Longer description for topic 1.",
            1);
        int topic2Rid = await InsertTopicAsync(
            "00000000000000000000000000000002",
            "Reference target widening",
            "Longer description for topic 2.",
            null);

        int group1Rid = await InsertGroupAsync(topic1Rid, "FHIR-1001", "Initial grouping rationale.", 0);

        await InsertMemberAsync(topic1Rid, group1Rid, "FHIR-1001", 0);
        await InsertMemberAsync(topic1Rid, null, "FHIR-1002", 1);
        await InsertMemberAsync(topic2Rid, null, "FHIR-1002", 0);

        await using SqliteConnection cp = new($"Data Source={dbPath};Pooling=False");
        await cp.OpenAsync();
        await using SqliteCommand checkpoint = cp.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await checkpoint.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task InlinedDb_TopicList_RoundTripsAllRows()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);
        await SeedTopicsAsync(scope.DbPath);

        int exit = await Program.Main(["--preparer-db", scope.DbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit);

        await using SqliteConnection conn = await OpenInlinedDbAsync(scope.OutDir);
        Assert.Equal(2, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_ticket_topics"));
        Assert.Equal(1, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_ticket_topic_groups"));
        Assert.Equal(3, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_ticket_topic_members"));

        // Mirror the SPA's Views.topics ORDER BY (no chip filter): hint
        // ASC with NULL last, then ShortDescription ASC.
        await using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT t.ShortDescription, " +
            "  (SELECT COUNT(*) FROM prepared_ticket_topic_groups g WHERE g.TopicRowId = t.RowId) AS GroupCount, " +
            "  (SELECT COUNT(*) FROM prepared_ticket_topic_members m WHERE m.TopicRowId = t.RowId) AS TicketCount " +
            "FROM prepared_ticket_topics t " +
            "ORDER BY (CASE WHEN t.RenderOrderHint IS NULL THEN 1 ELSE 0 END), " +
            "         t.RenderOrderHint ASC, t.ShortDescription ASC";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        List<(string Short, long Groups, long Tickets)> rows = [];
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }
        Assert.Equal(
            new[]
            {
                ("Observation status semantics", 1L, 2L),
                ("Reference target widening", 0L, 1L),
            },
            rows);
    }

    [Fact]
    public async Task InlinedDb_TopicDetail_PartitionsGroupAndOther()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);
        await SeedTopicsAsync(scope.DbPath);

        int exit = await Program.Main(["--preparer-db", scope.DbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit);

        await using SqliteConnection conn = await OpenInlinedDbAsync(scope.OutDir);

        // Look up topic 1's RowId by Id (mirrors how Views.topic loads it
        // from the URL parameter).
        long topicRowId;
        await using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT RowId FROM prepared_ticket_topics WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", "00000000000000000000000000000001");
            object? scalar = await cmd.ExecuteScalarAsync();
            Assert.NotNull(scalar);
            topicRowId = Convert.ToInt64(scalar);
        }

        // Mirror Views.topic's per-topic member query verbatim.
        await using SqliteCommand mcmd = conn.CreateCommand();
        mcmd.CommandText =
            "SELECT m.TopicGroupRowId, m.OrderInContainer, m.TicketKey, " +
            "       jst.Title, jst.Status, jst.Type " +
            "FROM prepared_ticket_topic_members m " +
            "LEFT JOIN jira_processing_source_tickets jst ON jst.Key = m.TicketKey " +
            "WHERE m.TopicRowId = @rid " +
            "ORDER BY m.OrderInContainer, m.TicketKey";
        mcmd.Parameters.AddWithValue("@rid", topicRowId);
        await using SqliteDataReader mreader = await mcmd.ExecuteReaderAsync();
        List<(long? Group, long Order, string Key)> rows = [];
        while (await mreader.ReadAsync())
        {
            long? group = mreader.IsDBNull(0) ? null : mreader.GetInt64(0);
            rows.Add((group, mreader.GetInt64(1), mreader.GetString(2)));
        }

        // Two members; FHIR-1001 in the group set, FHIR-1002 ungrouped.
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].Group);
        Assert.Equal(0, rows[0].Order);
        Assert.Equal("FHIR-1001", rows[0].Key);
        Assert.Null(rows[1].Group);
        Assert.Equal(1, rows[1].Order);
        Assert.Equal("FHIR-1002", rows[1].Key);
    }

    [Fact]
    public async Task InlinedDb_TrimDropsOrphanTopic_WhenFiltered()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath, ticketCount: 3);
        await SeedTopicsAsync(scope.DbPath);

        // Make ticket 3 belong to a different workgroup so the --wg trim
        // drops it. Seed a third topic whose only member is ticket 3 so
        // the trim leaves it orphaned (and the new Phase 1 logic must
        // remove it).
        await using (SqliteConnection mutate = new($"Data Source={scope.DbPath};Pooling=False"))
        {
            await mutate.OpenAsync();
            await using (SqliteCommand cmd = mutate.CreateCommand())
            {
                cmd.CommandText = "UPDATE jira_processing_source_tickets " +
                    "SET WorkGroup = @wg WHERE Key = 'FHIR-1003'";
                cmd.Parameters.AddWithValue("@wg", "Patient Administration");
                await cmd.ExecuteNonQueryAsync();
            }

            long topic3RowId;
            await using (SqliteCommand cmd = mutate.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO prepared_ticket_topics " +
                    "(Id, WorkGroupClean, WorkGroupDisplay, Specification, Type, " +
                    " ShortDescription, LongerDescription, RenderOrderHint, SavedAt) " +
                    "VALUES (@id, @wgc, @wgd, @spec, @type, @sd, @ld, @hint, @sa); " +
                    "SELECT last_insert_rowid()";
                cmd.Parameters.AddWithValue("@id", "00000000000000000000000000000003");
                cmd.Parameters.AddWithValue("@wgc", "pa");
                cmd.Parameters.AddWithValue("@wgd", "Patient Administration");
                cmd.Parameters.AddWithValue("@spec", "FHIR Core");
                cmd.Parameters.AddWithValue("@type", "Change Request");
                cmd.Parameters.AddWithValue("@sd", "Patient name handling");
                cmd.Parameters.AddWithValue("@ld", "Longer description for topic 3.");
                cmd.Parameters.AddWithValue("@hint", DBNull.Value);
                cmd.Parameters.AddWithValue("@sa", DateTimeOffset.UtcNow.ToString("O"));
                object? scalar = await cmd.ExecuteScalarAsync();
                topic3RowId = Convert.ToInt64(scalar);
            }

            long group3RowId;
            await using (SqliteCommand cmd = mutate.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO prepared_ticket_topic_groups " +
                    "(Id, TopicRowId, FirstTicketKey, Rationale, OrderInTopic, SavedAt) " +
                    "VALUES (@id, @trid, @ftk, @rat, 0, @sa); SELECT last_insert_rowid()";
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("@trid", topic3RowId);
                cmd.Parameters.AddWithValue("@ftk", "FHIR-1003");
                cmd.Parameters.AddWithValue("@rat", "Solo rationale.");
                cmd.Parameters.AddWithValue("@sa", DateTimeOffset.UtcNow.ToString("O"));
                object? scalar = await cmd.ExecuteScalarAsync();
                group3RowId = Convert.ToInt64(scalar);
            }

            await using (SqliteCommand cmd = mutate.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO prepared_ticket_topic_members " +
                    "(Id, TopicRowId, TopicGroupRowId, TicketKey, OrderInContainer) " +
                    "VALUES (@id, @trid, @grid, 'FHIR-1003', 0)";
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("@trid", topic3RowId);
                cmd.Parameters.AddWithValue("@grid", group3RowId);
                await cmd.ExecuteNonQueryAsync();
            }

            await using SqliteCommand checkpoint = mutate.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await checkpoint.ExecuteNonQueryAsync();
        }

        int exit = await Program.Main([
            "--preparer-db", scope.DbPath,
            "--out", scope.OutDir,
            "--wg", "FHIR Infrastructure",
        ]);
        Assert.Equal(0, exit);

        await using SqliteConnection conn = await OpenInlinedDbAsync(scope.OutDir);
        // Tickets 1 and 2 survive; ticket 3 is trimmed.
        Assert.Equal(2, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_tickets"));
        // Topics 1 and 2 survive; topic 3 (orphaned by the trim) is gone.
        Assert.Equal(2, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_ticket_topics"));
        Assert.Equal(0, await ReadCountAsync(conn,
            "SELECT COUNT(*) FROM prepared_ticket_topics WHERE Id = '00000000000000000000000000000003'"));
        // Group 1 survives (its member FHIR-1001 is still in the run);
        // group 3 is orphaned and dropped.
        Assert.Equal(1, await ReadCountAsync(conn, "SELECT COUNT(*) FROM prepared_ticket_topic_groups"));
        Assert.Equal(0, await ReadCountAsync(conn,
            "SELECT COUNT(*) FROM prepared_ticket_topic_groups WHERE FirstTicketKey = 'FHIR-1003'"));
        // Members for FHIR-1003 were removed by the per-ticket trim.
        Assert.Equal(0, await ReadCountAsync(conn,
            "SELECT COUNT(*) FROM prepared_ticket_topic_members WHERE TicketKey = 'FHIR-1003'"));
    }
}
