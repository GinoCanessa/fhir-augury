using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparerCutoverTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"preparer-cutover-{Guid.NewGuid():N}");

    [Fact]
    public async Task CutoverClassifiesCurrentRowsAndCreatesAllUnverifiedRun()
    {
        Directory.CreateDirectory(_directory);
        string databasePath = Path.Combine(_directory, "preparer.db");
        string backupPath = Path.Combine(_directory, "preparer.backup.db");
        using PreparerDatabase database = new(
            databasePath,
            NullLogger<PreparerDatabase>.Instance);
        database.Initialize();
        using (Microsoft.Data.Sqlite.SqliteConnection connection =
               database.OpenConnection())
        using (Microsoft.Data.Sqlite.SqliteCommand command =
               connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO authoring_mutation_fences(
                    ProcessorKind, RunId, LeaseId, AcquiredAt)
                VALUES(
                    'jira-fhir', 'maintenance:abandoned', 'lease',
                    '2026-09-01T00:00:00.0000000+00:00')
                """;
            command.ExecuteNonQuery();
        }
        database.AcquireStartupOwnership();
        Assert.Equal(
            1,
            await database.RecoverInterruptedMaintenanceLeasesAsync());
        using PreparerDatabase competing = new(
            databasePath,
            NullLogger<PreparerDatabase>.Instance);
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
                Status = "Triaged",
                WorkGroup = "FHIR-I",
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
        await database.SavePreparedTicketAsync(new PreparedTicketPayload
        {
            Key = "FHIR-1",
            RequestSummary = "request",
            ProposalA = "A",
            ProposalAImpact = PreparedTicketImpactValues.NonSubstantive,
            ProposalB = "B",
            ProposalBImpact = PreparedTicketImpactValues.NonSubstantive,
            ProposalC = "C",
            Recommendation = PreparedTicketRecommendationValues.ProposalA,
            RecommendationJustification = "because",
        });
        using (Microsoft.Data.Sqlite.SqliteConnection connection =
               database.OpenConnection())
        using (Microsoft.Data.Sqlite.SqliteCommand command =
               connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE prepared_ticket_authoring_state
                SET Classification = 'receipt-backed',
                    RunId = 'missing-run',
                    RunItemId = 'missing-item',
                    OperationId = 'missing-operation'
                WHERE TicketKey = 'FHIR-1'
                """;
            command.ExecuteNonQuery();
        }

        AuthoringProcessorModeRecord active =
            await new AuthoringCutoverCoordinator(database.OpenConnection)
                .ActivateAsync(
                    new AuthoringCutoverRequest(
                        "jira-fhir",
                        databasePath,
                        backupPath),
                    database);

        Assert.True(active.RevalidationRequired);
        Assert.Equal(
            "legacy-unverified",
            Scalar<string>(
                database,
                "SELECT Classification FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
        AuthoringRunStore store = new(database);
        AuthoringRunItemRecord item = Assert.Single(
            await store.GetRunItemsAsync(active.RevalidationRunId!));
        Assert.Equal("FHIR-1", item.BusinessKey);
        Assert.Equal(
            "2026-09-01T00:00:00.0000000+00:00",
            item.ExpectedSourceRevision);
        await Assert.ThrowsAsync<AuthoringConflictException>(() =>
            database.SavePreparedTicketAsync(new PreparedTicketPayload
            {
                Key = "FHIR-2",
                RequestSummary = "request",
                ProposalA = "A",
                ProposalAImpact = PreparedTicketImpactValues.NonSubstantive,
                ProposalB = "B",
                ProposalBImpact = PreparedTicketImpactValues.NonSubstantive,
                ProposalC = "C",
                Recommendation = PreparedTicketRecommendationValues.ProposalA,
                RecommendationJustification = "because",
            }));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static T Scalar<T>(PreparerDatabase database, string sql)
    {
        using Microsoft.Data.Sqlite.SqliteConnection connection =
            database.OpenConnection();
        using Microsoft.Data.Sqlite.SqliteCommand command =
            connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }
}
