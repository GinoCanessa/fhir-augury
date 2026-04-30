using System.IO;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processor.Jira.Fhir.Applier.Database;
using FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Tests.Database;

public class AppliedTicketQueueItemStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"applier-store-{Guid.NewGuid():N}.db");
    private readonly AppliedTicketQueueItemStore _store;

    public AppliedTicketQueueItemStoreTests()
    {
        _store = new AppliedTicketQueueItemStore(_path);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public async Task UpsertFromPlanner_InsertsNewRow()
    {
        AppliedTicketQueueItemUpsertResult result = await _store.UpsertFromPlannerAsync(
            "FHIR-1", "fhir", "cid-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, default);
        Assert.Equal(AppliedTicketQueueItemUpsertResult.Inserted, result);

        var row = await _store.GetByKeyAsync("FHIR-1", "fhir", default);
        Assert.NotNull(row);
        Assert.Equal("cid-a", row!.PlannerCompletionId);
        Assert.Null(row.ProcessingStatus);
    }

    [Fact]
    public async Task UpsertFromPlanner_UnchangedWhenSameCompletionId()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _store.UpsertFromPlannerAsync("FHIR-1", "fhir", "cid-a", now, now, default);
        AppliedTicketQueueItemUpsertResult result = await _store.UpsertFromPlannerAsync(
            "FHIR-1", "fhir", "cid-a", now, now.AddMinutes(5), default);
        Assert.Equal(AppliedTicketQueueItemUpsertResult.Unchanged, result);
    }

    [Fact]
    public async Task UpsertFromPlanner_MarksStaleWhenCompletedRowSeesNewPlan()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        await _store.UpsertFromPlannerAsync("FHIR-1", "fhir", "cid-a", start, start, default);
        var row = (await _store.GetByKeyAsync("FHIR-1", "fhir", default))!;
        Assert.True(await _store.ClaimItemAsync(row, start, default));
        await _store.MarkCompleteAsync(row, start.AddMinutes(1), default);

        AppliedTicketQueueItemUpsertResult result = await _store.UpsertFromPlannerAsync(
            "FHIR-1", "fhir", "cid-b", start.AddMinutes(2), start.AddMinutes(3), default);
        Assert.Equal(AppliedTicketQueueItemUpsertResult.MarkedStale, result);

        var refreshed = (await _store.GetByKeyAsync("FHIR-1", "fhir", default))!;
        Assert.Equal(ProcessingStatusValues.Stale, refreshed.ProcessingStatus);
        Assert.Null(refreshed.CompletionId);
        Assert.Equal("cid-b", refreshed.PlannerCompletionId);
        Assert.NotNull(refreshed.CompletedProcessingAt); // preserved for audit
    }

    [Fact]
    public async Task UpsertFromPlanner_UpdatesPlanReferenceForErrorOrNullRow()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        await _store.UpsertFromPlannerAsync("FHIR-1", "fhir", "cid-a", start, start, default);
        var row = (await _store.GetByKeyAsync("FHIR-1", "fhir", default))!;
        Assert.True(await _store.ClaimItemAsync(row, start, default));
        await _store.MarkErrorAsync(row, "boom", start.AddMinutes(1), default);

        AppliedTicketQueueItemUpsertResult result = await _store.UpsertFromPlannerAsync(
            "FHIR-1", "fhir", "cid-b", start.AddMinutes(2), start.AddMinutes(3), default);
        Assert.Equal(AppliedTicketQueueItemUpsertResult.UpdatedPlanReference, result);

        var refreshed = (await _store.GetByKeyAsync("FHIR-1", "fhir", default))!;
        Assert.Null(refreshed.ProcessingStatus);
        Assert.Equal(0, refreshed.ProcessingAttemptCount);
        Assert.Equal("cid-b", refreshed.PlannerCompletionId);
        Assert.Null(refreshed.ErrorSummary);
    }

    [Fact]
    public async Task UpsertFromPlanner_SkipsInProgressRow()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        await _store.UpsertFromPlannerAsync("FHIR-1", "fhir", "cid-a", start, start, default);
        var row = (await _store.GetByKeyAsync("FHIR-1", "fhir", default))!;
        Assert.True(await _store.ClaimItemAsync(row, start, default));

        AppliedTicketQueueItemUpsertResult result = await _store.UpsertFromPlannerAsync(
            "FHIR-1", "fhir", "cid-b", start.AddMinutes(2), start.AddMinutes(3), default);
        Assert.Equal(AppliedTicketQueueItemUpsertResult.SkippedInProgress, result);

        var refreshed = (await _store.GetByKeyAsync("FHIR-1", "fhir", default))!;
        Assert.Equal(ProcessingStatusValues.InProgress, refreshed.ProcessingStatus);
        Assert.Equal("cid-a", refreshed.PlannerCompletionId);
    }

    [Fact]
    public async Task ClaimItem_TransitionsPendingToInProgress()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        await _store.UpsertFromPlannerAsync("FHIR-1", "fhir", "cid-a", start, start, default);
        var row = (await _store.GetByKeyAsync("FHIR-1", "fhir", default))!;

        Assert.True(await _store.ClaimItemAsync(row, start.AddMinutes(1), default));
        Assert.Equal(ProcessingStatusValues.InProgress, row.ProcessingStatus);
        Assert.Equal(1, row.ProcessingAttemptCount);
        Assert.False(await _store.ClaimItemAsync(row, start.AddMinutes(2), default));
    }

    [Fact]
    public async Task MarkComplete_StampsCompletionIdIdempotently()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        await _store.UpsertFromPlannerAsync("FHIR-1", "fhir", "cid-a", start, start, default);
        var row = (await _store.GetByKeyAsync("FHIR-1", "fhir", default))!;
        Assert.True(await _store.ClaimItemAsync(row, start, default));

        await _store.MarkCompleteAsync(row, start.AddMinutes(1), default);
        string? firstStamp = row.CompletionId;
        Assert.False(string.IsNullOrEmpty(firstStamp));

        await _store.MarkCompleteAsync(row, start.AddMinutes(2), default);
        Assert.Equal(firstStamp, row.CompletionId);
    }

    [Fact]
    public async Task MarkStale_PreservesCompletedAtAndAuditFields()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        await _store.UpsertFromPlannerAsync("FHIR-1", "fhir", "cid-a", start, start, default);
        var row = (await _store.GetByKeyAsync("FHIR-1", "fhir", default))!;
        Assert.True(await _store.ClaimItemAsync(row, start, default));
        await _store.MarkCompleteAsync(row, start.AddMinutes(1), default);

        DateTimeOffset? completedAt = row.CompletedProcessingAt;
        DateTimeOffset? lastAttempt = row.LastProcessingAttemptAt;

        await _store.MarkStaleAsync(row, start.AddMinutes(2), default);

        var refreshed = (await _store.GetByKeyAsync("FHIR-1", "fhir", default))!;
        Assert.Equal(ProcessingStatusValues.Stale, refreshed.ProcessingStatus);
        Assert.Null(refreshed.CompletionId);
        Assert.Equal(completedAt, refreshed.CompletedProcessingAt);
        Assert.Equal(lastAttempt, refreshed.LastProcessingAttemptAt);
    }

    [Fact]
    public async Task GetPending_IncludesNullAndStaleRows()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        await _store.UpsertFromPlannerAsync("FHIR-1", "fhir", "cid-a", start, start, default);
        await _store.UpsertFromPlannerAsync("FHIR-2", "fhir", "cid-b", start.AddSeconds(1), start.AddSeconds(1), default);
        var row2 = (await _store.GetByKeyAsync("FHIR-2", "fhir", default))!;
        Assert.True(await _store.ClaimItemAsync(row2, start.AddSeconds(2), default));
        await _store.MarkCompleteAsync(row2, start.AddSeconds(3), default);
        await _store.MarkStaleAsync(row2, start.AddSeconds(4), default);

        IReadOnlyList<AppliedTicketQueueItemRecord> pending = await _store.GetPendingAsync(10, default);
        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, p => p.Key == "FHIR-1");
        Assert.Contains(pending, p => p.Key == "FHIR-2");
    }

    [Fact]
    public async Task GetQueueStats_CountsStaleAsPending()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        await _store.UpsertFromPlannerAsync("FHIR-1", "fhir", "cid-a", start, start, default);
        await _store.UpsertFromPlannerAsync("FHIR-2", "fhir", "cid-b", start, start, default);
        var row2 = (await _store.GetByKeyAsync("FHIR-2", "fhir", default))!;
        Assert.True(await _store.ClaimItemAsync(row2, start, default));
        await _store.MarkCompleteAsync(row2, start.AddMinutes(1), default);
        await _store.MarkStaleAsync(row2, start.AddMinutes(2), default);

        ProcessingQueueStats stats = await _store.GetQueueStatsAsync(default);
        Assert.Equal(2, stats.RemainingCount);
        Assert.Equal(0, stats.ProcessedCount);
    }

    [Fact]
    public async Task ResetOrphanedItems_ClearsInProgressOlderThanThreshold()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        await _store.UpsertFromPlannerAsync("FHIR-1", "fhir", "cid-a", start, start, default);
        var row = (await _store.GetByKeyAsync("FHIR-1", "fhir", default))!;
        Assert.True(await _store.ClaimItemAsync(row, start, default));

        int reset = await _store.ResetOrphanedItemsAsync(TimeSpan.FromSeconds(0), start.AddSeconds(1), default);
        Assert.Equal(1, reset);

        var refreshed = (await _store.GetByKeyAsync("FHIR-1", "fhir", default))!;
        Assert.Null(refreshed.ProcessingStatus);
    }
}
