using FhirAugury.Common.Database;
using FhirAugury.Server.Terminology.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Server.Terminology.Database;

/// <summary>
/// Terminology server SQLite database. Owns schema initialization for the
/// terminology_packages / artifacts / concepts / embeddings tables and
/// their FTS5 mirrors, plus the artifact-content FTS5 virtual table that
/// the lexical matcher (Phase 3) queries.
/// </summary>
public class TerminologyDatabase : SourceDatabase
{
    private readonly string? _ftsTokenizer;

    public TerminologyDatabase(string dbPath, ILogger<TerminologyDatabase> logger, bool readOnly = false, string? ftsTokenizer = null)
        : base(dbPath, logger, readOnly)
    {
        _ftsTokenizer = ftsTokenizer;
    }

    protected override void InitializeSchema(SqliteConnection connection)
    {
        TerminologyPackageRecord.CreateTable(connection);
        TerminologyArtifactRecord.CreateTable(connection);
        TerminologyConceptRecord.CreateTable(connection);
        TerminologyArtifactEmbeddingRecord.CreateTable(connection);

        // CsLightDbGen [LdgSQLiteIndex] has no Unique flag (see repo
        // memory). Enforce the (PackageId, ResolvedVersion) tuple
        // uniqueness with a follow-on index.
        EnsureUniqueIndex(connection,
            "ux_terminology_packages_id_version",
            "terminology_packages",
            "PackageId", "ResolvedVersion");

        EnsureUniqueIndex(connection,
            "ux_terminology_artifacts_url_version_fhir",
            "terminology_artifacts",
            "CanonicalUrlNormalized", "Version", "FhirVersion");

        CreateArtifactsFts(connection);
        CreateConceptsFts(connection);
    }

    private void CreateArtifactsFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "terminology_artifacts_fts",
            contentTable: "terminology_artifacts",
            contentRowId: "Id",
            indexedColumns: ["CanonicalUrl", "Title", "Name", "Description", "Purpose", "Keywords"],
            tokenizer: _ftsTokenizer);
    }

    private void CreateConceptsFts(SqliteConnection connection)
    {
        CreateFts5Table(
            connection,
            ftsTableName: "terminology_concepts_fts",
            contentTable: "terminology_concepts",
            contentRowId: "Id",
            indexedColumns: ["Code", "Display", "Definition"],
            tokenizer: _ftsTokenizer);
    }

    private static void EnsureUniqueIndex(SqliteConnection connection, string indexName, string table, params string[] columns)
    {
        string colList = string.Join(", ", columns);
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE UNIQUE INDEX IF NOT EXISTS {indexName} ON {table} ({colList});";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Drops all terminology tables and re-creates the schema.</summary>
    public void ResetDatabase(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenConnection();

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                DROP TABLE IF EXISTS terminology_artifacts_fts;
                DROP TABLE IF EXISTS terminology_concepts_fts;
                DROP TABLE IF EXISTS terminology_artifact_embeddings;
                DROP TABLE IF EXISTS terminology_concepts;
                DROP TABLE IF EXISTS terminology_artifacts;
                DROP TABLE IF EXISTS terminology_packages;
                """;
            cmd.ExecuteNonQuery();
        }

        InitializeSchema(connection);
    }
}
