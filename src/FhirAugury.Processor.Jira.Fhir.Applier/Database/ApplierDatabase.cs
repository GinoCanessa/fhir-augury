using FhirAugury.Processing.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Database;

public sealed class ApplierDatabase(string dbPath, ILogger<ApplierDatabase> logger, bool readOnly = false)
    : ProcessingDatabase(dbPath, logger, readOnly)
{
    public string DatabasePath { get; } = dbPath;

    protected override void InitializeSchema(SqliteConnection connection)
    {
        AppliedTicketQueueItemRecord.CreateTable(connection);
        AppliedTicketRecord.CreateTable(connection);
        AppliedTicketRepoRecord.CreateTable(connection);
        AppliedTicketRepoChangeRecord.CreateTable(connection);
        AppliedTicketOutputFileRecord.CreateTable(connection);
        RepoBaselineRecord.CreateTable(connection);

        // Composite uniqueness contracts not expressible via [LdgSQLiteUnique] / [LdgSQLiteIndex]:
        EnsureUniqueIndex(connection,
            "idx_applied_ticket_queue_items_key_shape",
            "applied_ticket_queue_items",
            ["Key", "SourceTicketShape"]);
        EnsureUniqueIndex(connection,
            "idx_applied_ticket_repos_issue_repo",
            "applied_ticket_repos",
            ["IssueKey", "RepoKey"]);
        EnsureUniqueIndex(connection,
            "idx_applied_ticket_output_files_issue_repo_path",
            "applied_ticket_output_files",
            ["IssueKey", "RepoKey", "RelativePath"]);
    }

    private static void EnsureUniqueIndex(SqliteConnection connection, string indexName, string table, IReadOnlyList<string> columns)
    {
        using SqliteCommand command = connection.CreateCommand();
        string columnList = string.Join(", ", columns);
        command.CommandText = $"CREATE UNIQUE INDEX IF NOT EXISTS {indexName} ON {table}({columnList});";
        command.ExecuteNonQuery();
    }
}
