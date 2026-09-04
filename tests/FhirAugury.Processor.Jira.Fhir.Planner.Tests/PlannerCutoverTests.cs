using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannerCutoverTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"planner-cutover-{Guid.NewGuid():N}");

    [Fact]
    public async Task CutoverRecapturesLegacyApplierCoordinates()
    {
        Directory.CreateDirectory(_directory);
        string databasePath = Path.Combine(_directory, "planner.db");
        using PlannerDatabase database = new(
            databasePath,
            NullLogger<PlannerDatabase>.Instance);
        database.AcquireStartupOwnership();
        database.Initialize();
        await database.RecoverInterruptedMaintenanceLeasesAsync();
        using PlannerDatabase competing = new(
            databasePath,
            NullLogger<PlannerDatabase>.Instance);
        Assert.Throws<InvalidOperationException>(
            competing.AcquireStartupOwnership);
        JiraProcessingSourceTicketStore source = new(databasePath);
        await source.UpsertAsync(
            new JiraIssueSummaryEntry
            {
                Key = "FHIR-1",
                ProjectKey = "FHIR",
                Title = "Title",
                Type = "Change Request",
                Status = "Resolved - change required",
                WorkGroup = "FHIR Infrastructure",
                Specification = "FHIR",
                UpdatedAt = new DateTimeOffset(
                    2026,
                    9,
                    1,
                    0,
                    0,
                    0,
                    TimeSpan.Zero),
            },
            "fhir",
            false,
            CancellationToken.None);
        DateTimeOffset completedAt =
            new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        Execute(
            database,
            """
            UPDATE jira_processing_source_tickets
            SET CompletionId = 'legacy-completion',
                CompletedProcessingAt = @completedAt,
                ProcessingStatus = 'complete'
            WHERE Key = 'FHIR-1'
            """,
            ("@completedAt", completedAt.ToString("O")));
        await database.SavePlannedTicketAsync(new PlannedTicketPayload
        {
            Key = "FHIR-1",
            Resolution = "Persuasive",
            ResolutionSummary = "summary",
        });
        Execute(
            database,
            """
            UPDATE planned_ticket_authoring_state
            SET Classification = 'receipt-backed',
                RunId = 'missing-run',
                RunItemId = 'missing-item',
                OperationId = 'missing-operation'
            WHERE TicketKey = 'FHIR-1'
            """);

        AuthoringProcessorModeRecord active =
            await new AuthoringCutoverCoordinator(database.OpenConnection)
                .ActivateAsync(
                    new AuthoringCutoverRequest(
                        "jira-fhir",
                        databasePath,
                        Path.Combine(_directory, "planner.backup.db")),
                    database);

        Assert.True(active.RevalidationRequired);
        Assert.Equal(
            "legacy-completion",
            Scalar<string>(
                database,
                "SELECT LegacyCompletionId FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
        Assert.Equal(
            completedAt,
            DateTimeOffset.Parse(
                Scalar<string>(
                    database,
                    "SELECT LegacyCompletedProcessingAt FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-1'")));
        AuthoringRunItemRecord item = Assert.Single(
            await new AuthoringRunStore(database).GetRunItemsAsync(
                active.RevalidationRunId!));
        Assert.Equal("FHIR-1", item.BusinessKey);
        await Assert.ThrowsAsync<AuthoringConflictException>(() =>
            database.SavePlannedTicketAsync(new PlannedTicketPayload
            {
                Key = "FHIR-2",
                ResolutionSummary = "blocked",
            }));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static void Execute(
        PlannerDatabase database,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(PlannerDatabase database, string sql)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }
}
