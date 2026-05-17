using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Tools.PreparerSite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.PreparerSite.Tests;

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

    private static async Task SeedPreparerDbAsync(string dbPath, int ticketCount = 2, int descriptionPaddingChars = 0)
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

        if (descriptionPaddingChars > 0)
        {
            // Seed jira_processing_source_tickets so prune drops a substantial
            // Description payload. Use raw SQL so we don't need to depend on a
            // higher-level Jira-side API.
            await using SqliteConnection connection = new($"Data Source={dbPath}");
            await connection.OpenAsync();
            string padding = new string('X', descriptionPaddingChars);
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
                cmd.Parameters.AddWithValue("@desc", padding);
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
        }

        // Force the WAL to merge back into the main DB so that the tool's raw
        // File.ReadAllBytes() sees everything just written.
        await using (SqliteConnection cp = new($"Data Source={dbPath}"))
        {
            await cp.OpenAsync();
            await using SqliteCommand cmd = cp.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await cmd.ExecuteNonQueryAsync();
        }
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
    public async Task Emit_PrunedDb_ShrinksOutput()
    {
        using TempScope full = new();
        using TempScope pruned = new();
        // 8 KB of Description per ticket × 4 tickets = ~32 KB of strippable data.
        await SeedPreparerDbAsync(full.DbPath, ticketCount: 4, descriptionPaddingChars: 8 * 1024);
        await SeedPreparerDbAsync(pruned.DbPath, ticketCount: 4, descriptionPaddingChars: 8 * 1024);

        int exitFull = await Program.Main(["--db", full.DbPath, "--out", full.OutDir]);
        int exitPruned = await Program.Main(["--db", pruned.DbPath, "--out", pruned.OutDir, "--prune"]);
        Assert.Equal(0, exitFull);
        Assert.Equal(0, exitPruned);

        long fullSize = new FileInfo(Path.Combine(full.OutDir, "index.html")).Length;
        long prunedSize = new FileInfo(Path.Combine(pruned.OutDir, "index.html")).Length;
        Assert.True(
            prunedSize < fullSize * 0.8,
            $"expected pruned ({prunedSize}) < 80% of full ({fullSize})");
    }

    [Fact]
    public async Task Emit_PrunedDb_PreservesTicketContent()
    {
        using TempScope scope = new();
        await SeedPreparerDbAsync(scope.DbPath, ticketCount: 2, descriptionPaddingChars: 1024);

        int exit = await Program.Main(["--db", scope.DbPath, "--out", scope.OutDir, "--prune"]);
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

            // Ticket bodies preserved.
            await using SqliteCommand select = conn.CreateCommand();
            select.CommandText = "SELECT RequestSummary, ProposalA, ProposalB, ProposalC, RecommendationJustification FROM prepared_tickets WHERE Key = @k";
            select.Parameters.AddWithValue("@k", "FHIR-1001");
            await using SqliteDataReader reader = await select.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Request summary body for ticket 1.", reader.GetString(0));
            Assert.Equal("Proposal A body for ticket 1.", reader.GetString(1));
            Assert.Equal("Proposal B body for ticket 1.", reader.GetString(2));
            Assert.Equal("Proposal C body for ticket 1.", reader.GetString(3));
            Assert.Equal("Recommendation justification 1.", reader.GetString(4));
            await reader.CloseAsync();

            // Source-ticket allowlist enforced — no Description column.
            await using SqliteCommand pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA table_info(jira_processing_source_tickets)";
            await using SqliteDataReader pragmaReader = await pragma.ExecuteReaderAsync();
            HashSet<string> columns = new(StringComparer.Ordinal);
            while (await pragmaReader.ReadAsync())
            {
                columns.Add(pragmaReader.GetString(1));
            }
            Assert.Equal(
                new HashSet<string>(["Key", "Title", "WorkGroup", "Status", "Type"], StringComparer.Ordinal),
                columns);
        }
        finally
        {
            try { File.Delete(tempDbPath); } catch { /* best-effort */ }
        }
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
