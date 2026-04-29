using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processing.Jira.Common.Tests.Database;

public class JiraProcessingSourceTicketStoreTests
{
    [Fact]
    public async Task Upsert_InsertsNewSourceTicket()
    {
        JiraProcessingSourceTicketStore store = CreateStore();

        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);

        Assert.Equal("FHIR-1", record.Key);
        Assert.Null(record.ProcessingStatus);
        Assert.NotNull(await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None));
    }

    [Fact]
    public async Task Upsert_UpdatesExistingTicketAndPreservesCompletedStatusUnlessResetRequested()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);
        await store.MarkCompleteAsync(record, DateTimeOffset.UtcNow, CancellationToken.None);

        JiraProcessingSourceTicketRecord updated = await store.UpsertAsync(CreateTicket("FHIR-1", title: "Updated"), "fhir", false, CancellationToken.None);

        Assert.Equal("Updated", updated.Title);
        Assert.Equal(ProcessingStatusValues.Complete, updated.ProcessingStatus);
    }

    [Fact]
    public async Task ResetForReprocessing_ClearsTimingStatusAndErrorColumns()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);
        await store.MarkErrorAsync(record, "failed", 42, DateTimeOffset.UtcNow, CancellationToken.None);

        JiraProcessingSourceTicketRecord? reset = await store.ResetForReprocessingAsync("FHIR-1", "fhir", CancellationToken.None);

        Assert.NotNull(reset);
        Assert.Null(reset.ProcessingStatus);
        Assert.Null(reset.ErrorMessage);
        Assert.Null(reset.AgentExitCode);
        Assert.Null(reset.StartedProcessingAt);
    }

    [Fact]
    public async Task TryClaimNext_ClaimsOnlyPendingRowsPassingFilters()
    {
        ResolvedJiraProcessingFilters filters = new() { TicketStatuses = ["Triaged"], SourceTicketShape = "fhir" };
        JiraProcessingSourceTicketStore store = CreateStore(filters);
        await store.UpsertAsync(CreateTicket("FHIR-1", status: "Triaged"), "fhir", false, CancellationToken.None);
        await store.UpsertAsync(CreateTicket("FHIR-2", status: "Submitted"), "fhir", false, CancellationToken.None);

        IReadOnlyList<JiraProcessingSourceTicketRecord> pending = await store.GetPendingAsync(10, CancellationToken.None);
        bool claimed = await store.ClaimItemAsync(pending[0], DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(claimed);
        Assert.Single(pending);
        Assert.Equal("FHIR-1", pending[0].Key);
    }

    [Fact]
    public async Task TryClaimNext_IsAtomicAcrossConcurrentCallers()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);

        Task<bool>[] claims = Enumerable.Range(0, 8)
            .Select(_ => store.ClaimItemAsync(record, DateTimeOffset.UtcNow, CancellationToken.None))
            .ToArray();
        bool[] results = await Task.WhenAll(claims);

        Assert.Equal(1, results.Count(static claimed => claimed));
    }

    [Fact]
    public async Task MarkComplete_SetsCompletedAtAndCompleteStatus()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);
        DateTimeOffset completedAt = DateTimeOffset.UtcNow;

        await store.MarkCompleteAsync(record, completedAt, CancellationToken.None);

        Assert.Equal(ProcessingStatusValues.Complete, record.ProcessingStatus);
        Assert.Equal(completedAt, record.CompletedProcessingAt);
    }

    [Fact]
    public async Task MarkError_SetsErrorStatusExitCodeAndMessage()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        JiraProcessingSourceTicketRecord record = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);

        await store.MarkErrorAsync(record, "boom", 7, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(ProcessingStatusValues.Error, record.ProcessingStatus);
        Assert.Equal("boom", record.ErrorMessage);
        Assert.Equal(7, record.AgentExitCode);
    }

    [Fact]
    public void Schema_HasRowIdPrimaryKeyAndIdUnique()
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"jira-processing-{Guid.NewGuid():N}.db");
        _ = new JiraProcessingSourceTicketStore(path);

        using SqliteConnection connection = new($"Data Source={path}");
        connection.Open();

        Dictionary<string, (int Pk, string Type)> columns = ReadTableInfo(connection, "jira_processing_source_tickets");

        Assert.True(columns.ContainsKey("RowId"), "RowId column missing");
        Assert.True(columns.ContainsKey("Id"), "Id column missing");
        Assert.Equal(1, columns["RowId"].Pk);
        Assert.Equal(0, columns["Id"].Pk);
        Assert.Contains("INT", columns["RowId"].Type, StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<(string Name, bool Unique)> indexes = ReadIndexes(connection, "jira_processing_source_tickets");
        Assert.Contains(indexes, i => i.Unique && IndexCovers(connection, i.Name, ["Id"]));
        Assert.Contains(indexes, i => i.Unique && IndexCovers(connection, i.Name, ["Key", "SourceTicketShape"]));
    }

    [Fact]
    public async Task Insert_AutoincrementsRowIdAndPreservesId()
    {
        JiraProcessingSourceTicketStore store = CreateStore();

        JiraProcessingSourceTicketRecord first = await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);
        JiraProcessingSourceTicketRecord second = await store.UpsertAsync(CreateTicket("FHIR-2"), "fhir", false, CancellationToken.None);

        JiraProcessingSourceTicketRecord? firstReloaded = await store.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None);
        JiraProcessingSourceTicketRecord? secondReloaded = await store.GetByKeyAsync("FHIR-2", "fhir", CancellationToken.None);

        Assert.NotNull(firstReloaded);
        Assert.NotNull(secondReloaded);
        Assert.NotEqual(0, firstReloaded.RowId);
        Assert.NotEqual(0, secondReloaded.RowId);
        Assert.NotEqual(firstReloaded.RowId, secondReloaded.RowId);
        Assert.Equal(first.Id, firstReloaded.Id);
        Assert.Equal(second.Id, secondReloaded.Id);
    }

    [Fact]
    public async Task ReadRecord_RoundTripsRowId()
    {
        JiraProcessingSourceTicketStore store = CreateStore();
        await store.UpsertAsync(CreateTicket("FHIR-1"), "fhir", false, CancellationToken.None);

        IReadOnlyList<JiraProcessingSourceTicketRecord> pending = await store.GetPendingAsync(10, CancellationToken.None);

        JiraProcessingSourceTicketRecord row = Assert.Single(pending);
        Assert.NotEqual(0, row.RowId);
    }

    private static Dictionary<string, (int Pk, string Type)> ReadTableInfo(SqliteConnection connection, string table)
    {
        Dictionary<string, (int Pk, string Type)> columns = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string name = reader.GetString(reader.GetOrdinal("name"));
            string type = reader.GetString(reader.GetOrdinal("type"));
            int pk = reader.GetInt32(reader.GetOrdinal("pk"));
            columns[name] = (pk, type);
        }

        return columns;
    }

    private static IReadOnlyList<(string Name, bool Unique)> ReadIndexes(SqliteConnection connection, string table)
    {
        List<(string Name, bool Unique)> indexes = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list({table});";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string name = reader.GetString(reader.GetOrdinal("name"));
            bool unique = reader.GetInt32(reader.GetOrdinal("unique")) == 1;
            indexes.Add((name, unique));
        }

        return indexes;
    }

    private static bool IndexCovers(SqliteConnection connection, string indexName, IReadOnlyList<string> expectedColumns)
    {
        List<string> actual = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_info({indexName});";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            actual.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        if (actual.Count != expectedColumns.Count)
        {
            return false;
        }

        for (int i = 0; i < actual.Count; i++)
        {
            if (!string.Equals(actual[i], expectedColumns[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static JiraProcessingSourceTicketStore CreateStore(ResolvedJiraProcessingFilters? filters = null)
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"jira-processing-{Guid.NewGuid():N}.db");
        return new JiraProcessingSourceTicketStore(path, filters);
    }

    private static JiraIssueSummaryEntry CreateTicket(string key, string title = "Title", string status = "Triaged") => new()
    {
        Key = key,
        ProjectKey = "FHIR",
        Title = title,
        Type = "Change Request",
        Status = status,
        WorkGroup = "Infrastructure",
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
