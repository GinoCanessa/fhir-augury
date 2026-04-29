using FhirAugury.Processing.Jira.Common.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Database;

public sealed class PlannerDatabase(string dbPath, ILogger<PlannerDatabase> logger, bool readOnly = false)
    : FhirAugury.Processing.Common.Database.ProcessingDatabase(dbPath, logger, readOnly)
{
    public string DatabasePath { get; } = dbPath;

    protected override void InitializeSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = JiraProcessingSourceTicketStore.SchemaSql;
        command.ExecuteNonQuery();
    }
}
