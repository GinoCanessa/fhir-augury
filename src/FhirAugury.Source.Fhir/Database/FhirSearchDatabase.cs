using FhirAugury.Common.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Fhir.Database;

/// <summary>
/// Writable sidecar database holding a <b>standalone</b> FTS5 index over FHIR
/// artifact name / title / description, plus a content table and a fingerprint
/// row. This is a disposable, rebuildable artifact — the read-only spec database
/// is never modified.
///
/// The FTS table is created with custom DDL (not <c>CreateFts5Table</c>), because
/// the external-content + trigger pattern requires a writable content table in
/// the same file. <c>ArtifactId</c> is stored as an <c>UNINDEXED</c> column so
/// search hits map back to <c>fhir_artifacts</c> without relying on the FTS
/// rowid.
/// </summary>
public sealed class FhirSearchDatabase : SourceDatabase
{
    private const string FingerprintKey = "source_fingerprint";
    private readonly string? _ftsTokenizer;

    public FhirSearchDatabase(string dbPath, ILogger<FhirSearchDatabase> logger, string? ftsTokenizer = null)
        : base(dbPath, logger, readOnly: false)
    {
        _ftsTokenizer = ftsTokenizer;
    }

    protected override void InitializeSchema(SqliteConnection connection)
    {
        // Omit the tokenize clause entirely when no tokenizer is configured.
        string tokenizeClause = _ftsTokenizer is not null ? $", tokenize='{_ftsTokenizer}'" : "";

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS fhir_artifacts (
                ArtifactId TEXT PRIMARY KEY,
                PackageKey INTEGER NOT NULL,
                Release TEXT NOT NULL,
                Kind TEXT NOT NULL,
                Name TEXT NOT NULL,
                Title TEXT,
                Url TEXT
            );
            CREATE INDEX IF NOT EXISTS IDX_fhir_artifacts_Release ON fhir_artifacts (Release);
            CREATE INDEX IF NOT EXISTS IDX_fhir_artifacts_Kind ON fhir_artifacts (Kind);

            CREATE VIRTUAL TABLE IF NOT EXISTS fhir_artifacts_fts USING fts5(
                Name, Title, Description, ArtifactId UNINDEXED{tokenizeClause}
            );

            CREATE TABLE IF NOT EXISTS fts_meta (
                Key TEXT PRIMARY KEY,
                Value TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Number of indexed artifacts (0 when the table is missing/empty).</summary>
    public int ArtifactCount()
    {
        try
        {
            using SqliteConnection conn = OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM fhir_artifacts";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    /// <summary>True when the artifact table has no rows.</summary>
    public bool IsEmpty() => ArtifactCount() == 0;

    /// <summary>Reads the stored source-database fingerprint, or null if absent.</summary>
    public string? GetFingerprint()
    {
        try
        {
            using SqliteConnection conn = OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM fts_meta WHERE Key = $k";
            cmd.Parameters.AddWithValue("$k", FingerprintKey);
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>Stores the source-database fingerprint within the supplied connection.</summary>
    public static void SetFingerprint(SqliteConnection conn, string fingerprint)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fts_meta (Key, Value) VALUES ($k, $v)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value
            """;
        cmd.Parameters.AddWithValue("$k", FingerprintKey);
        cmd.Parameters.AddWithValue("$v", fingerprint);
        cmd.ExecuteNonQuery();
    }
}
