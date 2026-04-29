using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processing.Jira.Common.Database;

public sealed class JiraProcessingDatabase(string dbPath, ILogger<JiraProcessingDatabase> logger, bool readOnly = false)
    : ProcessingDatabase(dbPath, logger, readOnly)
{
    public string DatabasePath { get; } = dbPath;

    protected override void InitializeSchema(SqliteConnection connection)
    {
        JiraProcessingSourceTicketRecord.CreateTable(connection);
        JiraProcessingSourceTicketStore.EnsureCompositeUniqueIndex(connection);
    }
}
