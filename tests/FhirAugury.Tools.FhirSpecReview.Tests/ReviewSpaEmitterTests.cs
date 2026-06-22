using System.Text.RegularExpressions;
using FhirAugury.Tools.FhirSpecReview.Database;
using FhirAugury.Tools.FhirSpecReview.Database.Records;
using FhirAugury.Tools.FhirSpecReview.Report;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.FhirSpecReview.Tests;

/// <summary>
/// Seeds a review DB and verifies <see cref="ReviewSpaEmitter"/> produces a
/// single self-contained SPA (<c>index.html</c> + <c>assets/</c>) with the
/// review DB inlined as base64, no stale per-WG HTML, and a WAL-safe snapshot.
/// Raw connections use <c>;Pooling=False</c>.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class ReviewSpaEmitterTests : IDisposable
{
    private readonly string _tempDir;

    public ReviewSpaEmitterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "spa-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    private string SeedReviewDb()
    {
        string dbPath = Path.Combine(_tempDir, "review.db");
        using ReviewDatabase db = new(dbPath, NullLogger<ReviewDatabase>.Instance);
        db.Initialize();
        using SqliteConnection conn = db.OpenConnection();

        conn.Insert(new ReviewRunRecord
        {
            Id = ReviewRunRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            BuildVersion = "6.0.0-test",
            BaselineRelease = "R5",
            RunAt = "2026-06-12T00:00:00Z",
        }, insertPrimaryKey: true);

        SpecPageRecord patientPage = new()
        {
            Id = SpecPageRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            ArtifactId = null,
            FhirArtifactId = null,
            PageFileName = "patient.html",
            ExistsInPublishIni = true,
            ExistsInSource = true,
            ResponsibleWorkGroupCode = "pa",
            ResponsibleWorkGroupName = "Patient Administration",
            SourceRelativePath = "source/patient.html",
            UnknownWordCount = 1,
        };
        conn.Insert(patientPage, insertPrimaryKey: true);

        conn.Insert(new SpecPageUnknownWordRecord
        {
            Id = SpecPageUnknownWordRecord.GetIndex(),
            PageId = patientPage.Id,
            Word = "Zorblax",
            IsTypo = false,
            Correction = null,
            ContextSnippet = "the Zorblax field is unknown",
        }, insertPrimaryKey: true);

        conn.Insert(new ArtifactRecord
        {
            Id = ArtifactRecord.GetIndex(),
            RepoFullName = "HL7/fhir",
            FhirId = "Patient",
            Name = "Patient",
            ArtifactType = "resource",
            ResponsibleWorkGroupCode = "pa",
            ResponsibleWorkGroupName = "Patient Administration",
        }, insertPrimaryKey: true);

        return dbPath;
    }

    [Fact]
    public void Emit_Writes_Single_Spa_With_Inlined_Db_And_Assets()
    {
        string dbPath = SeedReviewDb();
        string outDir = Path.Combine(_tempDir, "site");

        new ReviewSpaEmitter(dbPath).Emit(outDir);

        string indexPath = Path.Combine(outDir, "index.html");
        Assert.True(File.Exists(indexPath));
        string index = File.ReadAllText(indexPath);

        // Non-empty inlined base64 DB.
        string blob = ExtractDbBlob(index);
        Assert.False(string.IsNullOrEmpty(blob));

        // Provenance JSON inlined.
        Assert.Contains("window.__RUN__", index);
        Assert.Contains("6.0.0-test", index);

        // Assets present.
        Assert.True(File.Exists(Path.Combine(outDir, "assets", "sql-wasm.js")));
        Assert.True(File.Exists(Path.Combine(outDir, "assets", "sql-wasm.wasm")));
        Assert.True(File.Exists(Path.Combine(outDir, "assets", "app.js")));
        Assert.True(File.Exists(Path.Combine(outDir, "assets", "app.css")));

        // No per-WG static HTML files — only index.html at the root.
        string[] rootHtml = Directory.GetFiles(outDir, "*.html", SearchOption.TopDirectoryOnly);
        Assert.Single(rootHtml);
        Assert.Equal("index.html", Path.GetFileName(rootHtml[0]));
    }

    [Fact]
    public void Emit_Wal_Mode_Snapshot_Contains_Uncheckpointed_Rows()
    {
        // Seed via a WAL connection and do NOT checkpoint; the bare .db file
        // could miss the -wal rows, but the backup-based snapshot must not.
        string dbPath = SeedReviewDb();
        string outDir = Path.Combine(_tempDir, "wal-site");

        new ReviewSpaEmitter(dbPath).Emit(outDir);

        string index = File.ReadAllText(Path.Combine(outDir, "index.html"));
        byte[] dbBytes = Convert.FromBase64String(ExtractDbBlob(index));

        string snapPath = Path.Combine(_tempDir, "decoded.db");
        File.WriteAllBytes(snapPath, dbBytes);

        using SqliteConnection conn = new($"Data Source={snapPath};Pooling=False");
        conn.Open();
        Assert.Equal(1L, Count(conn, "SELECT COUNT(*) FROM pages WHERE PageFileName='patient.html'"));
        Assert.Equal(1L, Count(conn, "SELECT COUNT(*) FROM page_unknown_words WHERE Word='Zorblax'"));
        Assert.Equal(1L, Count(conn, "SELECT COUNT(*) FROM artifacts WHERE FhirId='Patient'"));
    }

    [Fact]
    public void Emit_Removes_Stale_PerWorkgroup_Html()
    {
        string dbPath = SeedReviewDb();
        string outDir = Path.Combine(_tempDir, "stale-site");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "pa.html"), "<html>stale</html>");
        File.WriteAllText(Path.Combine(outDir, "unassigned.html"), "<html>stale</html>");

        new ReviewSpaEmitter(dbPath).Emit(outDir);

        Assert.False(File.Exists(Path.Combine(outDir, "pa.html")));
        Assert.False(File.Exists(Path.Combine(outDir, "unassigned.html")));
        Assert.True(File.Exists(Path.Combine(outDir, "index.html")));
    }

    [Fact]
    public async Task ReportRunner_Overwrite_Guard()
    {
        string dbPath = SeedReviewDb();
        string outDir = Path.Combine(_tempDir, "guarded");

        ReportOptions first = new(dbPath, outDir, Force: false);
        Assert.Equal(0, await RunRedirectedAsync(first));

        ReportOptions second = new(dbPath, outDir, Force: false);
        Assert.Equal(1, await RunRedirectedAsync(second));

        ReportOptions forced = new(dbPath, outDir, Force: true);
        Assert.Equal(0, await RunRedirectedAsync(forced));
    }

    [Fact]
    public void Emit_App_Js_Ships_CopyForAi_Affordance()
    {
        string dbPath = SeedReviewDb();
        string outDir = Path.Combine(_tempDir, "copy-ai");

        new ReviewSpaEmitter(dbPath).Emit(outDir);

        string appJs = File.ReadAllText(Path.Combine(outDir, "assets", "app.js"));
        Assert.Contains("Copy for AI", appJs);
        Assert.Contains("copyForAi", appJs);
        Assert.Contains("installCopyButton", appJs);
        Assert.Contains("setCopyExport", appJs);
        Assert.Contains("clearCopyExport", appJs);
        Assert.Contains("execCommand", appJs);

        Assert.Contains("document.title", appJs);
        Assert.Contains("setDocTitle", appJs);

        string appCss = File.ReadAllText(Path.Combine(outDir, "assets", "app.css"));
        Assert.Contains(".copy-ai", appCss);
    }

    private static string ExtractDbBlob(string index)
    {
        Match m = Regex.Match(index, @"window\.__DB__='([^']*)'");
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    private static long Count(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static async Task<int> RunRedirectedAsync(ReportOptions options)
    {
        TextWriter origOut = Console.Out;
        TextWriter origErr = Console.Error;
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            return await ReportRunner.RunAsync(options).ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }
}
