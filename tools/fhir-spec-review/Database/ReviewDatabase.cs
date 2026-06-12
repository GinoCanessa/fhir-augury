using FhirAugury.Common.Database;
using FhirAugury.Tools.FhirSpecReview.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Tools.FhirSpecReview.Database;

/// <summary>
/// The review (output) SQLite database. Greenfield, augury-convention
/// cslightdbgen schema. <see cref="SourceDatabase.Initialize"/> creates each
/// table and the composite UNIQUE indexes (the <c>[LdgSQLiteIndex]</c>
/// attribute has no unique flag). Call <see cref="DropTables"/> first for a
/// clean re-run.
/// </summary>
public sealed class ReviewDatabase : SourceDatabase
{
    private static readonly string[] s_tableNames =
    [
        SpecPageImageRecord.DefaultTableName,
        SpecPageUnknownWordRecord.DefaultTableName,
        SpecPageRemovedFhirArtifactRecord.DefaultTableName,
        SpecPageRecord.DefaultTableName,
        ArtifactRecord.DefaultTableName,
        DuplicateArtifactKeyRecord.DefaultTableName,
        RemovedBaselineEntityRecord.DefaultTableName,
        WorkgroupRecord.DefaultTableName,
        ReviewRunRecord.DefaultTableName,
    ];

    public ReviewDatabase(string dbPath, ILogger logger, bool readOnly = false)
        : base(dbPath, logger, readOnly)
    {
    }

    protected override void InitializeSchema(SqliteConnection connection)
    {
        ArtifactRecord.CreateTable(connection);
        DuplicateArtifactKeyRecord.CreateTable(connection);
        SpecPageRecord.CreateTable(connection);
        SpecPageImageRecord.CreateTable(connection);
        SpecPageUnknownWordRecord.CreateTable(connection);
        SpecPageRemovedFhirArtifactRecord.CreateTable(connection);
        RemovedBaselineEntityRecord.CreateTable(connection);
        WorkgroupRecord.CreateTable(connection);
        ReviewRunRecord.CreateTable(connection);

        // [LdgSQLiteIndex] has no Unique flag — add composite UNIQUE indexes here
        // (repo convention) so re-inserts can SelectSingle-then-update cleanly.
        ExecuteNonQuery(connection,
            $"CREATE UNIQUE INDEX IF NOT EXISTS ux_artifacts_repo_fhirid ON {ArtifactRecord.DefaultTableName}({nameof(ArtifactRecord.RepoFullName)}, {nameof(ArtifactRecord.FhirId)})");
        ExecuteNonQuery(connection,
            $"CREATE UNIQUE INDEX IF NOT EXISTS ux_pages_repo_file ON {SpecPageRecord.DefaultTableName}({nameof(SpecPageRecord.RepoFullName)}, {nameof(SpecPageRecord.PageFileName)})");
        ExecuteNonQuery(connection,
            $"CREATE UNIQUE INDEX IF NOT EXISTS ux_removed_baseline_kind_name_release ON {RemovedBaselineEntityRecord.DefaultTableName}({nameof(RemovedBaselineEntityRecord.EntityKind)}, {nameof(RemovedBaselineEntityRecord.Name)}, {nameof(RemovedBaselineEntityRecord.BaselineRelease)})");
    }

    /// <summary>Drops every review table (children first) for a clean re-run.</summary>
    public void DropTables()
    {
        using SqliteConnection connection = OpenConnection();
        foreach (string table in s_tableNames)
        {
            ExecuteNonQuery(connection, $"DROP TABLE IF EXISTS \"{table}\"");
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
