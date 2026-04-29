using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;

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
