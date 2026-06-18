using FhirAugury.Source.Fhir.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Fhir.Indexing;

/// <summary>
/// Builds the standalone FTS5 sidecar index by reading artifact name / title /
/// description from the read-only spec database (structures, code systems, value
/// sets, operations, search parameters across every package) and bulk-inserting
/// into <c>fhir_artifacts</c> + <c>fhir_artifacts_fts</c> within a transaction.
/// A fingerprint of the source database is recorded so rebuilds are skipped when
/// the source is unchanged.
/// </summary>
public sealed class FhirSearchIndexBuilder(
    FhirSpecDatabase specDb,
    FhirSearchDatabase searchDb,
    ILogger<FhirSearchIndexBuilder> logger)
{
    private static readonly (string Table, string Kind)[] s_artifactTables =
    [
        ("Structures", "structure"),
        ("CodeSystems", "codesystem"),
        ("ValueSets", "valueset"),
        ("Operations", "operation"),
        ("SearchParameters", "searchparameter"),
    ];

    /// <summary>True when the index is empty or the source fingerprint has changed.</summary>
    public bool NeedsRebuild()
    {
        if (searchDb.IsEmpty())
        {
            return true;
        }
        string? stored = searchDb.GetFingerprint();
        return stored is null || stored != ComputeFingerprint();
    }

    /// <summary>A fingerprint of the source spec database (size + last-write time).</summary>
    public string ComputeFingerprint()
    {
        if (!specDb.Exists)
        {
            return string.Empty;
        }
        FileInfo info = new(specDb.DatabasePath);
        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    /// <summary>(Re)builds the sidecar index. Returns the number of indexed artifacts.</summary>
    public int Build(CancellationToken ct = default)
    {
        if (!specDb.Exists)
        {
            logger.LogWarning("Spec database not found at {Path}; FTS index not built", specDb.DatabasePath);
            return 0;
        }

        searchDb.Initialize();
        List<ArtifactRow> artifacts = ReadArtifacts(ct);

        using SqliteConnection conn = searchDb.OpenConnection();
        using SqliteTransaction tx = conn.BeginTransaction();

        ClearIndex(conn);
        InsertArtifacts(conn, artifacts, ct);
        FhirSearchDatabase.SetFingerprint(conn, ComputeFingerprint());

        tx.Commit();
        logger.LogInformation("Built FHIR FTS index with {Count} artifacts", artifacts.Count);
        return artifacts.Count;
    }

    private List<ArtifactRow> ReadArtifacts(CancellationToken ct)
    {
        List<ArtifactRow> artifacts = [];
        using SqliteConnection conn = specDb.OpenConnection();

        foreach ((string table, string kind) in s_artifactTables)
        {
            ct.ThrowIfCancellationRequested();
            using SqliteCommand cmd = conn.CreateCommand();
            // Table names are hard-coded literals — never user input.
            cmd.CommandText =
                $"SELECT t.PackageKey, p.ShortName, t.Key, t.Name, t.Title, t.Description, t.UnversionedUrl " +
                $"FROM {table} t JOIN Packages p ON p.Key = t.PackageKey";

            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                int packageKey = r.GetInt32(0);
                long key = r.GetInt64(2);
                artifacts.Add(new ArtifactRow(
                    ArtifactId: $"{kind}:{packageKey}:{key}",
                    PackageKey: packageKey,
                    Release: r.GetString(1),
                    Kind: kind,
                    Name: r.GetString(3),
                    Title: r.IsDBNull(4) ? null : r.GetString(4),
                    Description: r.IsDBNull(5) ? null : r.GetString(5),
                    Url: r.IsDBNull(6) ? null : r.GetString(6)));
            }
        }
        return artifacts;
    }

    private static void ClearIndex(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM fhir_artifacts; DELETE FROM fhir_artifacts_fts;";
        cmd.ExecuteNonQuery();
    }

    private static void InsertArtifacts(SqliteConnection conn, List<ArtifactRow> artifacts, CancellationToken ct)
    {
        using SqliteCommand artifactCmd = conn.CreateCommand();
        artifactCmd.CommandText = """
            INSERT OR REPLACE INTO fhir_artifacts (ArtifactId, PackageKey, Release, Kind, Name, Title, Url)
            VALUES ($id, $pk, $rel, $kind, $name, $title, $url)
            """;
        SqliteParameter aId = artifactCmd.Parameters.Add("$id", SqliteType.Text);
        SqliteParameter aPk = artifactCmd.Parameters.Add("$pk", SqliteType.Integer);
        SqliteParameter aRel = artifactCmd.Parameters.Add("$rel", SqliteType.Text);
        SqliteParameter aKind = artifactCmd.Parameters.Add("$kind", SqliteType.Text);
        SqliteParameter aName = artifactCmd.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter aTitle = artifactCmd.Parameters.Add("$title", SqliteType.Text);
        SqliteParameter aUrl = artifactCmd.Parameters.Add("$url", SqliteType.Text);

        using SqliteCommand ftsCmd = conn.CreateCommand();
        ftsCmd.CommandText = """
            INSERT INTO fhir_artifacts_fts (Name, Title, Description, ArtifactId)
            VALUES ($name, $title, $desc, $id)
            """;
        SqliteParameter fName = ftsCmd.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter fTitle = ftsCmd.Parameters.Add("$title", SqliteType.Text);
        SqliteParameter fDesc = ftsCmd.Parameters.Add("$desc", SqliteType.Text);
        SqliteParameter fId = ftsCmd.Parameters.Add("$id", SqliteType.Text);

        int processed = 0;
        foreach (ArtifactRow a in artifacts)
        {
            if ((processed++ & 0x3FF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            aId.Value = a.ArtifactId;
            aPk.Value = a.PackageKey;
            aRel.Value = a.Release;
            aKind.Value = a.Kind;
            aName.Value = a.Name;
            aTitle.Value = (object?)a.Title ?? DBNull.Value;
            aUrl.Value = (object?)a.Url ?? DBNull.Value;
            artifactCmd.ExecuteNonQuery();

            fName.Value = a.Name;
            fTitle.Value = (object?)a.Title ?? DBNull.Value;
            fDesc.Value = (object?)a.Description ?? DBNull.Value;
            fId.Value = a.ArtifactId;
            ftsCmd.ExecuteNonQuery();
        }
    }

    private readonly record struct ArtifactRow(
        string ArtifactId,
        int PackageKey,
        string Release,
        string Kind,
        string Name,
        string? Title,
        string? Description,
        string? Url);
}
