using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Database;

public sealed class PlannerDatabase(string dbPath, ILogger<PlannerDatabase> logger, bool readOnly = false)
    : FhirAugury.Processing.Common.Database.ProcessingDatabase(dbPath, logger, readOnly)
{
    public string DatabasePath { get; } = dbPath;

    protected override void InitializeSchema(SqliteConnection connection)
    {
        FhirAugury.Processing.Jira.Common.Database.Records.JiraProcessingSourceTicketRecord.CreateTable(connection);
        JiraProcessingSourceTicketStore.EnsureCompositeUniqueIndex(connection);
        PlannedTicketRecord.CreateTable(connection);
        PlannedTicketRepoRecord.CreateTable(connection);
        PlannedTicketRepoChangeRecord.CreateTable(connection);
        PlannedTicketRepoImpactRecord.CreateTable(connection);
        PlannedTicketChangeValidationRecord.CreateTable(connection);
        PlannedTicketTestingConsiderationRecord.CreateTable(connection);
        PlannedTicketOpenQuestionRecord.CreateTable(connection);
    }

    public async Task DeletePlanForTicketAsync(string issueKey, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await DeletePlanForTicketAsync(connection, issueKey, ct);
    }

    public static async Task DeletePlanForTicketAsync(SqliteConnection connection, string issueKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issueKey);
        foreach (string table in DeleteOrder)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = table == "planned_tickets"
                ? $"DELETE FROM {table} WHERE Key = @issueKey"
                : $"DELETE FROM {table} WHERE IssueKey = @issueKey";
            command.Parameters.Add(new SqliteParameter("@issueKey", issueKey));
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<bool> PlanExistsAsync(string issueKey, CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM planned_tickets WHERE Key = @issueKey LIMIT 1";
        command.Parameters.Add(new SqliteParameter("@issueKey", issueKey));
        object? value = await command.ExecuteScalarAsync(ct);
        return value is not null;
    }

    private static readonly string[] DeleteOrder =
    [
        "planned_ticket_open_questions",
        "planned_ticket_testing_considerations",
        "planned_ticket_change_validations",
        "planned_ticket_repo_impacts",
        "planned_ticket_repo_changes",
        "planned_ticket_repos",
        "planned_tickets",
    ];
}
