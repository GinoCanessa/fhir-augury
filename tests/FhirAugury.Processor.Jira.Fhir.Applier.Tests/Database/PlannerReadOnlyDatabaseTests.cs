using System.IO;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Applier.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Database;

/// <summary>
/// Builds a minimal Planner-shape database fixture in a temp file. The shape mirrors the
/// columns the applier reads (we only need <c>Key</c>, <c>Type</c>, <c>ProcessingStatus</c>,
/// <c>CompletionId</c>, <c>CompletedProcessingAt</c> from <c>jira_processing_source_tickets</c>
/// and <c>Key</c> from <c>planned_tickets</c>).
/// </summary>
public static class PlannerFixture
{
    public static string CreateTempPath() =>
        Path.Combine(Path.GetTempPath(), $"applier-planner-{Guid.NewGuid():N}.db");

    public static void CreateSchema(string dbPath)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE jira_processing_source_tickets (
                Id TEXT PRIMARY KEY,
                Key TEXT NOT NULL,
                Type TEXT NOT NULL,
                ProcessingStatus TEXT,
                CompletionId TEXT,
                CompletedProcessingAt TEXT
            );
            CREATE TABLE planned_tickets (
                Id TEXT PRIMARY KEY,
                Key TEXT NOT NULL UNIQUE
            );
            """;
        command.ExecuteNonQuery();
    }

    public static void InsertJiraTicket(string dbPath, string key, string type, string? status, string? completionId, DateTimeOffset? completedAt)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO jira_processing_source_tickets (Id, Key, Type, ProcessingStatus, CompletionId, CompletedProcessingAt) VALUES (@id, @key, @type, @status, @cid, @completed);";
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("@cid", (object?)completionId ?? DBNull.Value);
        command.Parameters.AddWithValue("@completed", completedAt is null ? DBNull.Value : (object)completedAt.Value.ToString("O"));
        command.ExecuteNonQuery();
    }

    public static void InsertPlannedTicket(string dbPath, string key)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO planned_tickets (Id, Key) VALUES (@id, @key);";
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@key", key);
        command.ExecuteNonQuery();
    }
}

public class PlannerReadOnlyDatabaseTests : IDisposable
{
    private readonly string _path = PlannerFixture.CreateTempPath();

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void ListCompletedPlannedTickets_ThrowsWithClearMessageWhenFileMissing()
    {
        PlannerReadOnlyDatabase planner = new(_path, NullLogger<PlannerReadOnlyDatabase>.Instance);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => planner.ListCompletedPlannedTickets(null));
        Assert.Contains("Planner database not found", ex.Message);
    }

    [Fact]
    public void ListCompletedPlannedTickets_ReturnsOnlyCompletedAndPlanned()
    {
        PlannerFixture.CreateSchema(_path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        PlannerFixture.InsertJiraTicket(_path, "FHIR-1", "Change Request", "complete", "cid-1", now);
        PlannerFixture.InsertPlannedTicket(_path, "FHIR-1");

        // No planned_tickets row → excluded
        PlannerFixture.InsertJiraTicket(_path, "FHIR-2", "Change Request", "complete", "cid-2", now);

        // Not yet complete → excluded
        PlannerFixture.InsertJiraTicket(_path, "FHIR-3", "Change Request", "in-progress", null, null);
        PlannerFixture.InsertPlannedTicket(_path, "FHIR-3");

        // Wrong type when filter is applied → excluded by filter
        PlannerFixture.InsertJiraTicket(_path, "FHIR-4", "Question", "complete", "cid-4", now);
        PlannerFixture.InsertPlannedTicket(_path, "FHIR-4");

        PlannerReadOnlyDatabase planner = new(_path, NullLogger<PlannerReadOnlyDatabase>.Instance);

        IReadOnlyList<PlannerCompletedTicketView> all = planner.ListCompletedPlannedTickets(null);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, v => v.Key == "FHIR-1" && v.CompletionId == "cid-1");
        Assert.Contains(all, v => v.Key == "FHIR-4");

        IReadOnlyList<PlannerCompletedTicketView> filtered = planner.ListCompletedPlannedTickets(new[] { "Change Request" });
        Assert.Single(filtered);
        Assert.Equal("FHIR-1", filtered[0].Key);
    }
}
