using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.FhirSpecReview.Report;

/// <summary>
/// Emits a single self-contained report: an <c>index.html</c> plus an
/// <c>assets/</c> folder. The review SQLite DB is inlined as base64 into
/// <c>window.__DB__</c> and loaded in-browser via sql.js (no network), modelled
/// on ticket-site's <c>PreparerSubSiteEmitter</c>. Read-only consumer of the
/// review DB.
/// </summary>
internal sealed class ReviewSpaEmitter
{
    private const string ReportPrefix = "web-assets/report/";
    private const string TemplateName = "web-assets/report/index.template.html";
    private const string TitleMarker = "<!-- __TITLE__ -->";
    private const string DbBlobMarker = "<!-- __DB_BLOB__ -->";
    private const string ProvenanceMarker = "<!-- __PROVENANCE__ -->";

    private const string BaseTitle = "FHIR Spec Review";

    private readonly string _reviewDbPath;

    public ReviewSpaEmitter(string reviewDbPath)
    {
        _reviewDbPath = reviewDbPath;
    }

    public void Emit(string outDir)
    {
        // Force/overwrite cleanup: delete the output directory so stale per-WG
        // *.html files from a prior static run never linger next to the SPA.
        if (Directory.Exists(outDir))
        {
            Directory.Delete(outDir, recursive: true);
        }
        Directory.CreateDirectory(outDir);
        string assetsDir = Path.Combine(outDir, "assets");
        Directory.CreateDirectory(assetsDir);

        byte[] dbBytes = SnapshotDbBytes(_reviewDbPath);
        string base64 = Convert.ToBase64String(GzipBytes(dbBytes));
        string blobScript = $"<script>window.__DB__='{base64}';window.__DBGZ__=1;</script>";
        string provenanceScript = BuildProvenanceScript(_reviewDbPath);
        string encodedTitle = WebUtility.HtmlEncode(BaseTitle);

        Assembly asm = typeof(ReviewSpaEmitter).Assembly;
        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(ReportPrefix, StringComparison.Ordinal)) continue;

            using Stream stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing embedded resource: {name}");

            if (string.Equals(name, TemplateName, StringComparison.Ordinal))
            {
                using StreamReader reader = new(stream);
                string template = reader.ReadToEnd();
                string html = template
                    .Replace(TitleMarker, encodedTitle, StringComparison.Ordinal)
                    .Replace(ProvenanceMarker, provenanceScript, StringComparison.Ordinal)
                    .Replace(DbBlobMarker, blobScript, StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(outDir, "index.html"), html);
            }
            else
            {
                string relative = name.Substring(ReportPrefix.Length);
                string outFile = Path.Combine(assetsDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
                using FileStream fs = File.Create(outFile);
                stream.CopyTo(fs);
            }
        }
    }

    /// <summary>
    /// Produces a coherent single-file snapshot of the (WAL-mode) review DB via
    /// the SQLite Online Backup API, then VACUUMs and reads the bytes. Reading
    /// the bare <c>.db</c> file could miss uncheckpointed <c>-wal</c> data; the
    /// backup copies the source connection's full logical view.
    /// </summary>
    private static byte[] GzipBytes(byte[] raw)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(raw, 0, raw.Length);
        }
        return output.ToArray();
    }

    private static byte[] SnapshotDbBytes(string reviewDbPath)
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), "fhir-spec-review-snap-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            string sourceConnStr = new SqliteConnectionStringBuilder
            {
                DataSource = reviewDbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ConnectionString;
            string destConnStr = new SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ConnectionString;

            using (SqliteConnection source = new(sourceConnStr))
            using (SqliteConnection dest = new(destConnStr))
            {
                source.Open();
                dest.Open();
                source.BackupDatabase(dest);

                using SqliteCommand vacuum = dest.CreateCommand();
                vacuum.CommandText = "VACUUM;";
                vacuum.ExecuteNonQuery();
            }

            return File.ReadAllBytes(tempPath);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (IOException) { /* best effort temp cleanup */ }
        }
    }

    /// <summary>
    /// Reads the latest <c>review_runs</c> row and emits a
    /// <c>window.__RUN__</c> JSON object for the SPA provenance header.
    /// </summary>
    private static string BuildProvenanceScript(string reviewDbPath)
    {
        string connStr = new SqliteConnectionStringBuilder
        {
            DataSource = reviewDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString;

        Dictionary<string, string?>? run = null;
        using (SqliteConnection conn = new(connStr))
        {
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT RepoFullName, BuildVersion, BaselineRelease, RunAt FROM review_runs ORDER BY Id DESC LIMIT 1";
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                run = new Dictionary<string, string?>
                {
                    ["repo"] = reader.IsDBNull(0) ? null : reader.GetString(0),
                    ["build"] = reader.IsDBNull(1) ? null : reader.GetString(1),
                    ["baseline"] = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ["runAt"] = reader.IsDBNull(3) ? null : reader.GetString(3),
                };
            }
        }

        string json = run is null ? "null" : JsonSerializer.Serialize(run);
        return $"<script>window.__RUN__={json};</script>";
    }
}
