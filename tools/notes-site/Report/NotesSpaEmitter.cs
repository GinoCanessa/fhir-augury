using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.NotesSite.Report;

/// <summary>
/// Emits a single self-contained report: an <c>index.html</c> plus an
/// <c>assets/</c> folder. The notes SQLite DB is inlined as base64 into
/// <c>window.__DB__</c> and loaded in-browser via sql.js (no network), modelled
/// on fhir-spec-review's <c>ReviewSpaEmitter</c>. Read-only consumer of the
/// notes DB.
/// </summary>
internal sealed class NotesSpaEmitter
{
    private const string ReportPrefix = "web-assets/report/";
    private const string TemplateName = "web-assets/report/index.template.html";
    private const string TitleMarker = "<!-- __TITLE__ -->";
    private const string DbBlobMarker = "<!-- __DB_BLOB__ -->";
    private const string ProvenanceMarker = "<!-- __PROVENANCE__ -->";

    private readonly string _notesDbPath;
    private readonly string _title;

    public NotesSpaEmitter(string notesDbPath, string title)
    {
        _notesDbPath = notesDbPath;
        _title = title;
    }

    public void Emit(string outDir)
    {
        // Force/overwrite cleanup: delete the output directory so stale assets
        // from a prior run never linger next to the SPA.
        if (Directory.Exists(outDir))
        {
            Directory.Delete(outDir, recursive: true);
        }
        Directory.CreateDirectory(outDir);
        string assetsDir = Path.Combine(outDir, "assets");
        Directory.CreateDirectory(assetsDir);

        byte[] dbBytes = SnapshotDbBytes(_notesDbPath);
        string base64 = Convert.ToBase64String(dbBytes);
        string blobScript = $"<script>window.__DB__='{base64}';</script>";
        string provenanceScript = BuildProvenanceScript(_notesDbPath);
        string encodedTitle = WebUtility.HtmlEncode(_title);

        Assembly asm = typeof(NotesSpaEmitter).Assembly;
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
    /// Produces a coherent single-file snapshot of the (WAL-mode) notes DB via
    /// the SQLite Online Backup API, then VACUUMs and reads the bytes. Reading
    /// the bare <c>.db</c> file could miss uncheckpointed <c>-wal</c> data; the
    /// backup copies the source connection's full logical view.
    /// </summary>
    private static byte[] SnapshotDbBytes(string notesDbPath)
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(), "notes-site-snap-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            string sourceConnStr = new SqliteConnectionStringBuilder
            {
                DataSource = notesDbPath,
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
    /// Reads the latest <c>notes_runs</c> row plus a note count and emits a
    /// <c>window.__RUN__</c> JSON object for the SPA provenance header.
    /// </summary>
    private static string BuildProvenanceScript(string notesDbPath)
    {
        string connStr = new SqliteConnectionStringBuilder
        {
            DataSource = notesDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString;

        string? repoOwner = null, repoName = null, repoCategory = null;
        string? sinceShortSha = null, headShortSha = null, runAt = null, windowLabel = null;
        int noteCount = 0;

        using (SqliteConnection conn = new(connStr))
        {
            conn.Open();

            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT RepoOwner, RepoName, RepoCategory, SinceShortSha, HeadShortSha, RunAt, WindowLabel " +
                    "FROM notes_runs ORDER BY RunAt DESC, RowId DESC LIMIT 1";
                using SqliteDataReader reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    repoOwner = reader.IsDBNull(0) ? null : reader.GetString(0);
                    repoName = reader.IsDBNull(1) ? null : reader.GetString(1);
                    repoCategory = reader.IsDBNull(2) ? null : reader.GetString(2);
                    sinceShortSha = reader.IsDBNull(3) ? null : reader.GetString(3);
                    headShortSha = reader.IsDBNull(4) ? null : reader.GetString(4);
                    runAt = reader.IsDBNull(5) ? null : reader.GetString(5);
                    windowLabel = reader.IsDBNull(6) ? null : reader.GetString(6);
                }
            }

            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM notes";
                noteCount = Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms))
        {
            writer.WriteStartObject();
            WriteNullableString(writer, "repoOwner", repoOwner);
            WriteNullableString(writer, "repoName", repoName);
            WriteNullableString(writer, "repoCategory", repoCategory);
            WriteNullableString(writer, "sinceShortSha", sinceShortSha);
            WriteNullableString(writer, "headShortSha", headShortSha);
            WriteNullableString(writer, "runAt", runAt);
            WriteNullableString(writer, "windowLabel", windowLabel);
            writer.WriteNumber("noteCount", noteCount);
            writer.WriteEndObject();
        }

        string json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        return $"<script>window.__RUN__={json};</script>";
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }
}
