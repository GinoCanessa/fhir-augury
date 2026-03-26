using FhirAugury.Common.Database;
using FhirAugury.Orchestrator.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Orchestrator.Database;

public class OrchestratorDatabase : SourceDatabase
{
    public OrchestratorDatabase(string dbPath, ILogger<OrchestratorDatabase> logger, bool readOnly = false)
        : base(dbPath, logger, readOnly) { }

    protected override void InitializeSchema(SqliteConnection connection)
    {
        XrefScanStateRecord.CreateTable(connection);
    }

    public void ResetDatabase()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS xref_scan_state;
            """;
        cmd.ExecuteNonQuery();
        InitializeSchema(connection);
    }
}
