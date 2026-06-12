using FhirAugury.Tools.FhirSpecReview.Database;
using FhirAugury.Tools.FhirSpecReview.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.FhirSpecReview.Tests;

/// <summary>
/// CLI smoke tests for the <c>fhir-spec-review</c> entry point. Redirects the
/// console, so it joins the shared <c>ConsoleRedirect</c> collection to keep
/// console redirection serialized across parallel xUnit test classes. Raw
/// connections use <c>;Pooling=False</c>.
/// </summary>
[Collection("ConsoleRedirect")]
public sealed class CliSmokeTests : IDisposable
{
    private readonly string _tempDir;

    public CliSmokeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cli-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    private static async Task<int> RunAsync(params string[] args)
    {
        TextWriter origOut = Console.Out;
        TextWriter origErr = Console.Error;
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            return await Program.Main(args).ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    [Fact]
    public async Task Help_Exits_Zero()
    {
        Assert.Equal(0, await RunAsync("--help"));
    }

    [Fact]
    public async Task UnknownVerb_Exits_NonZero()
    {
        Assert.NotEqual(0, await RunAsync("frobnicate"));
    }

    [Fact]
    public async Task NoArgs_Exits_NonZero()
    {
        Assert.NotEqual(0, await RunAsync());
    }

    [Fact]
    public async Task Report_Verb_Emits_Single_Spa_With_Assets()
    {
        string dbPath = Path.Combine(_tempDir, "review.db");
        using (ReviewDatabase db = new(dbPath, NullLogger<ReviewDatabase>.Instance))
        {
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
        }

        string outDir = Path.Combine(_tempDir, "site");
        int exit = await RunAsync("report", "--review-db", dbPath, "--out", outDir, "--force");
        Assert.Equal(0, exit);

        // Single index.html at the root, no per-WG *.html, plus the assets folder.
        string[] rootHtml = Directory.GetFiles(outDir, "*.html", SearchOption.TopDirectoryOnly);
        Assert.Single(rootHtml);
        Assert.Equal("index.html", Path.GetFileName(rootHtml[0]));
        Assert.True(File.Exists(Path.Combine(outDir, "assets", "sql-wasm.js")));
        Assert.True(File.Exists(Path.Combine(outDir, "assets", "app.js")));
    }
}
