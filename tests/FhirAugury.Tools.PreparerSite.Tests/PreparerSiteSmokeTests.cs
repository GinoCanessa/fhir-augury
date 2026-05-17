using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Tools.PreparerSite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.PreparerSite.Tests;

[Collection("ConsoleRedirect")]
public sealed class PreparerSiteSmokeTests
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
        await using SqliteConnection connection = new($"Data Source={dbPath}");
        await connection.OpenAsync();
        for (int i = 1; i <= ticketCount; i++)
        {
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO jira_processing_source_tickets " +
                "(Id, Key, Title, Description, Project, Status, WorkGroup, Type, SourceTicketShape, LastSyncedAt, LastUpdated, ProcessingAttemptCount, ProcessingStatus) " +
                "VALUES (@id, @key, @title, @desc, @project, @status, @wg, @type, @shape, @synced, @updated, @pac, @ps)";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("@key", $"FHIR-{1000 + i}");
            cmd.Parameters.AddWithValue("@title", $"Source ticket title {i}");
            cmd.Parameters.AddWithValue("@desc", DBNull.Value);
            cmd.Parameters.AddWithValue("@project", "FHIR");
            cmd.Parameters.AddWithValue("@status", "Open");
            cmd.Parameters.AddWithValue("@wg", "FHIR Infrastructure");
            cmd.Parameters.AddWithValue("@type", "Change Request");
            cmd.Parameters.AddWithValue("@shape", "default");
            cmd.Parameters.AddWithValue("@synced", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@updated", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@pac", 0);
            cmd.Parameters.AddWithValue("@ps", "Done");
            await cmd.ExecuteNonQueryAsync();
        }

        // Force the WAL to merge back into the main DB so that the tool's raw
        // File.ReadAllBytes() sees everything just written.
        await using SqliteConnection cp = new($"Data Source={dbPath}");
        await cp.OpenAsync();
        await using SqliteCommand checkpoint = cp.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await checkpoint.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Emit_WritesIndexHtml_WithInlinedDb()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);

        int exit = await Program.Main(["--db", scope.DbPath, "--out", scope.OutDir]);

        Assert.Equal(0, exit);
        string indexPath = Path.Combine(scope.OutDir, "index.html");
        Assert.True(File.Exists(indexPath));
        string html = await File.ReadAllTextAsync(indexPath);
        Assert.Contains("window.__DB__='", html, StringComparison.Ordinal);

        long dbSize = new FileInfo(scope.DbPath).Length;
        long expectedMin = (long)(dbSize * 4.0 / 3.0) - 16;
        long expectedMax = (long)(dbSize * 4.0 / 3.0) + 16;
        int start = html.IndexOf("window.__DB__='", StringComparison.Ordinal) + "window.__DB__='".Length;
        int end = html.IndexOf('\'', start);
        int b64Length = end - start;
        Assert.InRange(b64Length, expectedMin, expectedMax);
    }

    [Fact]
    public async Task Emit_CopiesAssets()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);

        int exit = await Program.Main(["--db", scope.DbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit);

        string assetsDir = Path.Combine(scope.OutDir, "assets");
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
        int exit = await Program.Main(["--db", scope.DbPath, "--out", scope.OutDir, "--title", customTitle]);
        Assert.Equal(0, exit);

        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "index.html"));
        Assert.Contains($"<title>{customTitle}</title>", html, StringComparison.Ordinal);
        Assert.Contains($"<h1>{customTitle}</h1>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<title>Preparer Report</title>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emit_UsesDefaultTitle_WhenOmitted()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);

        int exit = await Program.Main(["--db", scope.DbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit);

        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "index.html"));
        Assert.Contains("<title>Preparer Report</title>", html, StringComparison.Ordinal);
        Assert.Contains("<h1>Preparer Report</h1>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emit_HydratedDb_InlinesHydrationTables_RoundTrip()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath);
        await SeedHydrationAsync(scope.DbPath);

        int exit = await Program.Main(["--db", scope.DbPath, "--out", scope.OutDir]);
        Assert.Equal(0, exit);

        string html = await File.ReadAllTextAsync(Path.Combine(scope.OutDir, "index.html"));
        string base64 = ExtractInlinedDbBase64(html);
        byte[] dbBytes = Convert.FromBase64String(base64);

        string tempDbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await File.WriteAllBytesAsync(tempDbPath, dbBytes);

            await using SqliteConnection conn = new($"Data Source={tempDbPath};Mode=ReadOnly");
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
            try { File.Delete(tempDbPath); } catch { /* best-effort */ }
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
        await using SqliteConnection connection = new($"Data Source={dbPath}");
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

        await using SqliteConnection cp = new($"Data Source={dbPath}");
        await cp.OpenAsync();
        await using SqliteCommand checkpoint = cp.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await checkpoint.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task EmitSite_OnLegacyDb_WithNoHydrate_FailsFast()
    {
        using TempScope scope = new();
        await LegacyPreparerTestDb.SeedAsync(scope.DbPath,
        [
            new PreparerTestDb.SourceTicketSeed("FHIR-1001", Project: "FHIR"),
            new PreparerTestDb.SourceTicketSeed("OTHER-2001", Project: "OTHER"),
        ]);

        StringWriter capturedErr = new();
        StringWriter capturedOut = new();
        TextWriter originalErr = Console.Error;
        TextWriter originalOut = Console.Out;
        Console.SetError(capturedErr);
        Console.SetOut(capturedOut);
        int exit;
        try
        {
            exit = await Program.Main(["--db", scope.DbPath, "--out", scope.OutDir, "--project", "FHIR", "--no-hydrate"]);
        }
        finally
        {
            Console.SetError(originalErr);
            Console.SetOut(originalOut);
        }

        string stderr = capturedErr.ToString();
        Assert.NotEqual(0, exit);
        Assert.Contains("Hydration is missing", stderr, StringComparison.Ordinal);
        Assert.Contains(scope.DbPath, stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(scope.OutDir, "index.html")));

        // Source DB must be untouched: preflight is purely diagnostic under --no-hydrate.
        await using SqliteConnection sourceConn = new($"Data Source={scope.DbPath};Mode=ReadOnly");
        await sourceConn.OpenAsync();
        await using SqliteCommand cmd = sourceConn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'prepared_ticket_hydration'";
        long sourceCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        Assert.Equal(0, sourceCount);
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
            exit = await Program.Main(["--db", scope.DbPath, "--out", scope.OutDir, "--prune"]);
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
            exit = await Program.Main(["--db", bogus, "--out", scope.OutDir]);
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
        int exit;
        try
        {
            exit = await Program.Main(["--db", scope.DbPath, "--out", scope.OutDir]);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        Assert.NotEqual(0, exit);
        string stderr = capturedErr.ToString();
        Assert.Contains("schema", stderr, StringComparison.OrdinalIgnoreCase);
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
}
