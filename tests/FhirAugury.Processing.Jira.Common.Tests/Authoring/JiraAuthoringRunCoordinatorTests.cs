using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Tests.Authoring;

public sealed class JiraAuthoringRunCoordinatorTests
{
    [Fact]
    public async Task CreateScheduledRun_LegacyModeIsNotActivatable()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.SeedAsync("FHIR-1", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => fixture.Coordinator.CreateScheduledRunAsync());

        Assert.Equal(AuthoringConflictCode.AuthoringNotActivated, conflict.Code);
    }

    [Fact]
    public async Task CreateScheduledRun_FreezesRevisionAndIgnoresLegacyCompletion()
    {
        using JiraAuthoringTestFixture fixture = new();
        DateTimeOffset firstRevision = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        JiraProcessingSourceTicketRecord source = await fixture.SeedAsync("FHIR-1", firstRevision);
        await fixture.SourceStore.MarkCompleteAsync(source, DateTimeOffset.UtcNow, CancellationToken.None);
        await fixture.ActivateAsync();

        JiraAuthoringRunCreation first =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;
        Assert.Equal(AuthoringStatusValues.Runs.Queued, first.Run.Status);
        Assert.Equal(firstRevision.ToString("O"), Assert.Single(first.Items).ExpectedSourceRevision);
        Assert.Null(await fixture.Coordinator.CreateScheduledRunAsync());

        DateTimeOffset secondRevision = firstRevision.AddDays(1);
        await fixture.SeedAsync("FHIR-1", secondRevision, title: "Updated");
        JiraAuthoringRunCreation second =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;

        Assert.Equal(AuthoringStatusValues.Runs.Queued, second.Run.Status);
        Assert.Equal(secondRevision.ToString("O"), Assert.Single(second.Items).ExpectedSourceRevision);
        Assert.Equal(
            firstRevision.ToString("O"),
            Assert.Single(await fixture.AuthoringStore.GetRunItemsAsync(first.Run.Id)).ExpectedSourceRevision);
    }

    [Fact]
    public async Task CreateRun_DeduplicatesRepeatedBusinessKeys()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        JiraProcessingSourceTicketRecord source = await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        JiraAuthoringRunCreation creation = await fixture.Coordinator.CreateRunAsync(
            [source, source],
            databaseOnly: true);

        Assert.Single(creation.Items);
    }

    [Fact]
    public async Task CreateExplicitRun_RejectsSecondActiveRunAtCapacity()
    {
        using JiraAuthoringTestFixture fixture = new(maxActiveAuthoringRuns: 1);
        await fixture.ActivateAsync();
        DateTimeOffset revision = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        JiraProcessingSourceTicketRecord first =
            await fixture.SeedAsync("FHIR-1", revision);
        JiraProcessingSourceTicketRecord second =
            await fixture.SeedAsync("FHIR-2", revision);
        JiraAuthoringRunCreation active =
            await fixture.Coordinator.CreateExplicitRunAsync([first], databaseOnly: false);

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => fixture.Coordinator.CreateExplicitRunAsync(
                    [second],
                    databaseOnly: false));

        Assert.Equal(AuthoringConflictCode.ActiveRunCapacityReached, conflict.Code);
        Assert.Contains(active.Run.Id, conflict.Message, StringComparison.Ordinal);
        Assert.Equal([active.Run.Id], conflict.RelatedRunIds);
        Assert.Equal(
            active.Run.Id,
            (await fixture.AuthoringStore.GetOldestQueuedRunAsync(
                fixture.Coordinator.ProcessorKind))!.Id);
    }

    [Fact]
    public async Task CreateExplicitRun_RevisionConflictReportsRunIds()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        DateTimeOffset revision =
            new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        JiraProcessingSourceTicketRecord first =
            await fixture.SeedAsync("FHIR-1", revision);
        JiraProcessingSourceTicketRecord second =
            await fixture.SeedAsync("FHIR-2", revision);
        JiraProcessingSourceTicketRecord third =
            await fixture.SeedAsync("FHIR-3", revision);
        JiraAuthoringRunCreation existing =
            await fixture.Coordinator.CreateExplicitRunAsync(
                [first, second],
                databaseOnly: false);

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => fixture.Coordinator.CreateExplicitRunAsync(
                    [first, third],
                    databaseOnly: false));

        Assert.Equal(
            AuthoringConflictCode.RevisionAlreadyScheduled,
            conflict.Code);
        Assert.Equal([existing.Run.Id], conflict.RelatedRunIds);
    }

    [Fact]
    public async Task CreateExplicitRun_AfterTerminalRun_PersistsRequestForRestart()
    {
        using JiraAuthoringTestFixture fixture = new(maxActiveAuthoringRuns: 1);
        await fixture.ActivateAsync();
        DateTimeOffset revision = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        JiraProcessingSourceTicketRecord completedTicket =
            await fixture.SeedAsync("FHIR-1", revision);
        JiraAuthoringRunCreation completed =
            await fixture.Coordinator.CreateExplicitRunAsync(
                [completedTicket],
                databaseOnly: true);
        await CompleteDatabaseOnlyRunAsync(fixture, completed);

        JiraProcessingSourceTicketRecord supersededTicket =
            await fixture.SeedAsync("FHIR-2", revision);
        JiraAuthoringRunCreation superseded =
            await fixture.Coordinator.CreateExplicitRunAsync(
                [supersededTicket],
                databaseOnly: false);
        await fixture.AuthoringStore.SupersedeRunAsync(
            superseded.Run.Id,
            "Test terminal transition.");

        JiraProcessingSourceTicketRecord restartedTicket =
            await fixture.SeedAsync("FHIR-3", revision);
        JiraAuthoringRunCreation accepted =
            await fixture.Coordinator.CreateExplicitRunAsync(
                [restartedTicket],
                databaseOnly: false);
        AuthoringRunStore restartedStore = new(fixture.SourceStoreConnection);
        AuthoringRunRecord restored =
            (await restartedStore.GetRunAsync(accepted.Run.Id))!;
        JiraAuthoringRunRequestSnapshot request =
            JsonSerializer.Deserialize<JiraAuthoringRunRequestSnapshot>(
                restored.RequestJson!,
                JsonSerializerOptions.Web)!;

        Assert.Equal(accepted.Run.Id, restored.Id);
        Assert.Equal(["FHIR-3"], request.TicketKeys);
        Assert.False(request.DatabaseOnly);
    }

    [Fact]
    public async Task CreateScheduledRun_ConcurrentAdmissionCreatesOneRevisionRun()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        JiraAuthoringRunCreation?[] results = await Task.WhenAll(
            fixture.Coordinator.CreateScheduledRunAsync(),
            fixture.Coordinator.CreateScheduledRunAsync());

        Assert.Single(results, result => result is not null);
        Assert.Null(results.SingleOrDefault(result => result is null));
        using Microsoft.Data.Sqlite.SqliteConnection connection =
            fixture.SourceStoreConnection();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM authoring_run_items";
        Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public async Task CreateRun_SupersedesStaleQueuedRunBeforeNewerRevision()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        DateTimeOffset revision = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", revision);
        JiraAuthoringRunCreation first =
            (await fixture.Coordinator.CreateScheduledRunAsync(databaseOnly: true))!;

        await fixture.SeedAsync("FHIR-1", revision.AddDays(1), title: "Second");
        JiraAuthoringRunCreation second =
            (await fixture.Coordinator.CreateScheduledRunAsync(databaseOnly: true))!;
        Assert.Equal(AuthoringStatusValues.Runs.Queued, second.Run.Status);
        await CompleteDatabaseOnlyRunAsync(fixture, first);

        await fixture.SeedAsync("FHIR-1", revision.AddDays(2), title: "Third");
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            second.Run.Id));
        AuthoringRunReconciliationResult reconciliation =
            await fixture.Coordinator.ReconcileRunAsync(
                (await fixture.AuthoringStore.GetRunAsync(second.Run.Id))!,
                CancellationToken.None);
        AuthoringRunRecord third =
            (await fixture.AuthoringStore.GetOldestQueuedRunAsync(
                fixture.Coordinator.ProcessorKind))!;

        Assert.Equal(
            AuthoringRunReconciliationOutcome.Superseded,
            reconciliation.Outcome);
        Assert.Equal(
            AuthoringStatusValues.Runs.Superseded,
            (await fixture.AuthoringStore.GetRunAsync(second.Run.Id))!.Status);
        Assert.Equal(
            AuthoringStatusValues.Runs.Queued,
            third.Status);
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            third.Id));
        JiraAuthoringWorkItemStore queue = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem current = Assert.Single(
            await queue.GetPendingAsync(third.Id, 10, CancellationToken.None));
        Assert.Equal(revision.AddDays(2).ToString("O"), current.RunItem.ExpectedSourceRevision);
    }

    [Fact]
    public async Task StaleBatchItemDoesNotStrandCurrentSibling()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        DateTimeOffset revision = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", revision);
        await fixture.SeedAsync("FHIR-2", revision);
        JiraAuthoringRunCreation batch =
            (await fixture.Coordinator.CreateScheduledRunAsync(databaseOnly: true))!;
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            batch.Run.Id));

        await fixture.SeedAsync("FHIR-1", revision.AddDays(1), title: "Updated");
        await fixture.Coordinator.ReconcileRunAsync(
            (await fixture.AuthoringStore.GetRunAsync(batch.Run.Id))!,
            CancellationToken.None);
        JiraAuthoringWorkItemStore queue = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem current = Assert.Single(
            await queue.GetPendingAsync(batch.Run.Id, 10, CancellationToken.None));
        AuthoringRunItemRecord[] batchItems =
            (await fixture.AuthoringStore.GetRunItemsAsync(batch.Run.Id)).ToArray();

        Assert.Equal("FHIR-2", current.SourceTicket.Key);
        Assert.Equal(
            AuthoringStatusValues.Items.Superseded,
            batchItems.Single(item => item.BusinessKey == "FHIR-1").Status);
        Assert.Equal(
            AuthoringStatusValues.Items.Pending,
            batchItems.Single(item => item.BusinessKey == "FHIR-2").Status);

        JiraAuthoringRunCreation replacement =
            (await fixture.Coordinator.CreateScheduledRunAsync(databaseOnly: true))!;
        Assert.Equal("FHIR-1", Assert.Single(replacement.Items).BusinessKey);
        Assert.Equal(AuthoringStatusValues.Runs.Queued, replacement.Run.Status);
    }

    [Fact]
    public async Task StaleInitialRevalidationIsReplacedWithCurrentRevision()
    {
        using JiraAuthoringTestFixture fixture = new();
        DateTimeOffset firstRevision =
            new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", firstRevision);
        await fixture.SeedAsync("FHIR-2", firstRevision);
        AuthoringProcessorModeRecord active =
            await new AuthoringCutoverCoordinator(
                fixture.SourceStoreConnection).ActivateAsync(
                new AuthoringCutoverRequest(
                    "jira-fhir",
                    fixture.DatabasePath,
                    $"{fixture.DatabasePath}.pre-cutover"),
                new StaticCutoverParticipant(
                [
                    new("FHIR-1", "fhir", firstRevision.ToString("O")),
                    new("FHIR-2", "fhir", firstRevision.ToString("O")),
                ]));
        AuthoringRunItemRecord completed =
            (await fixture.AuthoringStore.GetRunItemsAsync(
                active.RevalidationRunId!))
            .Single(item => item.BusinessKey == "FHIR-1");
        AuthoringOperationClaim claim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    active.RevalidationRunId!,
                    completed.Id));
        AuthoringReceiptAcceptance receipt =
            await fixture.AuthoringStore.AcceptResultAsync(
                new AuthoringResultSubmission(
                    active.RevalidationRunId!,
                    completed.Id,
                    claim.OperationId,
                    completed.ExpectedSourceRevision,
                    AuthoringResultHasher.HashNormalizedUtf8("first")),
                claim.OperationToken);
        await fixture.AuthoringStore.MarkItemCompleteAsync(
            completed.Id,
            receipt.Receipt.ReceiptId);

        DateTimeOffset secondRevision = firstRevision.AddDays(1);
        await fixture.SeedAsync(
            "FHIR-2",
            secondRevision,
            title: "Updated");

        Assert.True(await fixture.Coordinator.SupersedeStaleItemsAsync(
            active.RevalidationRunId!));
        AuthoringProcessorModeRecord refreshed =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        Assert.NotEqual(
            active.RevalidationRunId,
            refreshed.RevalidationRunId);
        AuthoringRunItemRecord replacement = Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(
                refreshed.RevalidationRunId!));
        Assert.Equal("FHIR-2", replacement.BusinessKey);
        Assert.Equal(secondRevision.ToString("O"), replacement.ExpectedSourceRevision);
        IReadOnlyList<AuthoringRunItemRecord> predecessor =
            await fixture.AuthoringStore.GetRunItemsAsync(
                active.RevalidationRunId!);
        Assert.Equal(
            AuthoringStatusValues.Items.Complete,
            predecessor.Single(item => item.BusinessKey == "FHIR-1").Status);
        Assert.Equal(
            AuthoringStatusValues.Items.Superseded,
            predecessor.Single(item => item.BusinessKey == "FHIR-2").Status);

        DateTimeOffset thirdRevision = firstRevision.AddDays(2);
        await fixture.SeedAsync(
            "FHIR-1",
            thirdRevision,
            title: "Updated again");
        await using (Microsoft.Data.Sqlite.SqliteConnection connection =
                     fixture.SourceStoreConnection())
        {
            AuthoringConflictException staleCorpus =
                await Assert.ThrowsAsync<AuthoringConflictException>(() =>
                    JiraProcessingSourceTicketStore
                        .EnsureRunSourceRevisionsCurrentAsync(
                            connection,
                            refreshed.RevalidationRunId!,
                            CancellationToken.None));
            Assert.Equal(
                AuthoringConflictCode.SourceRevisionMismatch,
                staleCorpus.Code);
        }

        Assert.True(await fixture.Coordinator.SupersedeStaleItemsAsync(
            refreshed.RevalidationRunId!));
        AuthoringProcessorModeRecord secondRefresh =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        IReadOnlyList<AuthoringRunItemRecord> secondReplacement =
            await fixture.AuthoringStore.GetRunItemsAsync(
                secondRefresh.RevalidationRunId!);
        Assert.Equal(
            ["FHIR-1", "FHIR-2"],
            secondReplacement
                .Select(item => item.BusinessKey)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            thirdRevision.ToString("O"),
            secondReplacement
                .Single(item => item.BusinessKey == "FHIR-1")
                .ExpectedSourceRevision);
    }

    [Fact]
    public async Task InitialRevalidation_UnchangedRevisionPreservesAttemptBudgetAcrossReplacement()
    {
        using JiraAuthoringTestFixture fixture = new();
        DateTimeOffset firstRevision =
            new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", firstRevision);
        await fixture.SeedAsync("FHIR-2", firstRevision);
        AuthoringProcessorModeRecord active =
            await new AuthoringCutoverCoordinator(
                fixture.SourceStoreConnection).ActivateAsync(
                new AuthoringCutoverRequest(
                    "jira-fhir",
                    fixture.DatabasePath,
                    $"{fixture.DatabasePath}.pre-cutover"),
                new StaticCutoverParticipant(
                [
                    new("FHIR-1", "fhir", firstRevision.ToString("O")),
                    new("FHIR-2", "fhir", firstRevision.ToString("O")),
                ]));
        AuthoringRunItemRecord firstItem =
            (await fixture.AuthoringStore.GetRunItemsAsync(
                active.RevalidationRunId!))
            .Single(item => item.BusinessKey == "FHIR-1");
        DateTimeOffset now =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringOperationClaim firstClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    active.RevalidationRunId!,
                    firstItem.Id,
                    now));
        await fixture.AuthoringStore.MarkClaimErrorAsync(
            firstItem.Id,
            firstClaim.OperationId,
            "first failure",
            now.AddSeconds(1));
        await fixture.AuthoringStore.ReconcileErroredItemsAsync(
            active.RevalidationRunId!,
            now.AddMinutes(2));
        AuthoringOperationClaim secondClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    active.RevalidationRunId!,
                    firstItem.Id,
                    now.AddMinutes(2)));
        await fixture.AuthoringStore.MarkClaimErrorAsync(
            firstItem.Id,
            secondClaim.OperationId,
            "second failure",
            now.AddMinutes(2).AddSeconds(1));

        await fixture.SeedAsync(
            "FHIR-2",
            firstRevision.AddDays(1),
            title: "Updated B");
        Assert.True(await fixture.Coordinator.SupersedeStaleItemsAsync(
            active.RevalidationRunId!));
        AuthoringProcessorModeRecord firstReplacementMode =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        AuthoringRunItemRecord unchangedReplacement =
            (await fixture.AuthoringStore.GetRunItemsAsync(
                firstReplacementMode.RevalidationRunId!))
            .Single(item => item.BusinessKey == "FHIR-1");

        Assert.Equal(2, unchangedReplacement.AttemptCount);
        AuthoringOperationClaim thirdClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    unchangedReplacement.RunId,
                    unchangedReplacement.Id,
                    now.AddMinutes(3)));
        Assert.Equal(3, thirdClaim.AttemptNumber);

        DateTimeOffset newRevision = firstRevision.AddDays(2);
        await fixture.SeedAsync(
            "FHIR-1",
            newRevision,
            title: "Updated A");
        Assert.True(await fixture.Coordinator.SupersedeStaleItemsAsync(
            unchangedReplacement.RunId));
        AuthoringConflictException exhaustedCoordinate =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => fixture.AuthoringStore.ClaimItemAsync(
                    unchangedReplacement.RunId,
                    unchangedReplacement.Id,
                    now.AddMinutes(4)));
        Assert.Equal(
            AuthoringConflictCode.RunNotActive,
            exhaustedCoordinate.Code);

        AuthoringProcessorModeRecord secondReplacementMode =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        AuthoringRunItemRecord changedRevision =
            (await fixture.AuthoringStore.GetRunItemsAsync(
                secondReplacementMode.RevalidationRunId!))
            .Single(item => item.BusinessKey == "FHIR-1");
        AuthoringOperationClaim changedRevisionClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    changedRevision.RunId,
                    changedRevision.Id,
                    now.AddMinutes(5)));

        Assert.Equal(newRevision.ToString("O"), changedRevision.ExpectedSourceRevision);
        Assert.Equal(0, changedRevision.AttemptCount);
        Assert.Equal(1, changedRevisionClaim.AttemptNumber);
    }

    [Fact]
    public async Task StaleInitialRevalidationWaitsForAcceptedPostPersistenceWork()
    {
        using JiraAuthoringTestFixture fixture = new();
        DateTimeOffset firstRevision =
            new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", firstRevision);
        await fixture.SeedAsync("FHIR-2", firstRevision);
        AuthoringProcessorModeRecord active =
            await new AuthoringCutoverCoordinator(
                fixture.SourceStoreConnection).ActivateAsync(
                new AuthoringCutoverRequest(
                    "jira-fhir",
                    fixture.DatabasePath,
                    $"{fixture.DatabasePath}.pre-cutover"),
                new StaticCutoverParticipant(
                [
                    new("FHIR-1", "fhir", firstRevision.ToString("O")),
                    new("FHIR-2", "fhir", firstRevision.ToString("O")),
                ]));
        IReadOnlyList<AuthoringRunItemRecord> initialItems =
            await fixture.AuthoringStore.GetRunItemsAsync(
                active.RevalidationRunId!);
        AuthoringRunItemRecord persisted =
            initialItems.Single(item => item.BusinessKey == "FHIR-2");
        AuthoringOperationClaim claim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    active.RevalidationRunId!,
                    persisted.Id));
        AuthoringReceiptAcceptance receipt =
            await fixture.AuthoringStore.AcceptResultAsync(
                new AuthoringResultSubmission(
                    active.RevalidationRunId!,
                    persisted.Id,
                    claim.OperationId,
                    persisted.ExpectedSourceRevision,
                    AuthoringResultHasher.HashNormalizedUtf8("persisted")),
                claim.OperationToken);
        await fixture.SeedAsync(
            "FHIR-1",
            firstRevision.AddDays(1),
            title: "Updated");

        Assert.False(await fixture.Coordinator.SupersedeStaleItemsAsync(
            active.RevalidationRunId!));
        JiraAuthoringWorkItemStore workItems = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem pending = Assert.Single(
            await workItems.GetPendingAsync(
                active.RevalidationRunId!,
                10,
                CancellationToken.None));
        Assert.Equal("FHIR-2", pending.RunItem.BusinessKey);
        Assert.Equal(
            AuthoringStatusValues.Items.Persisted,
            pending.RunItem.Status);

        await fixture.AuthoringStore.MarkItemCompleteAsync(
            persisted.Id,
            receipt.Receipt.ReceiptId);
        Assert.True(await fixture.Coordinator.SupersedeStaleItemsAsync(
            active.RevalidationRunId!));
        AuthoringProcessorModeRecord refreshed =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        Assert.Equal(
            "FHIR-1",
            Assert.Single(
                await fixture.AuthoringStore.GetRunItemsAsync(
                    refreshed.RevalidationRunId!)).BusinessKey);
        Assert.Equal(
            AuthoringStatusValues.Items.Complete,
            (await fixture.AuthoringStore.GetRunItemsAsync(
                active.RevalidationRunId!))
            .Single(item => item.BusinessKey == "FHIR-2")
            .Status);
    }

    private static async Task CompleteDatabaseOnlyRunAsync(
        JiraAuthoringTestFixture fixture,
        JiraAuthoringRunCreation creation)
    {
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        AuthoringRunItemRecord item = Assert.Single(creation.Items);
        AuthoringOperationClaim claim = (await fixture.AuthoringStore.ClaimItemAsync(
            creation.Run.Id,
            item.Id))!;
        AuthoringReceiptAcceptance receipt = await fixture.AuthoringStore.AcceptResultAsync(
            new AuthoringResultSubmission(
                creation.Run.Id,
                item.Id,
                claim.OperationId,
                item.ExpectedSourceRevision,
                AuthoringResultHasher.HashNormalizedUtf8("payload")),
            claim.OperationToken);
        await fixture.AuthoringStore.MarkItemCompleteAsync(item.Id, receipt.Receipt.ReceiptId);
        await fixture.AuthoringStore.MarkRunFinalizingAsync(creation.Run.Id);
        await fixture.AuthoringStore.CompleteRunAsync(creation.Run.Id, snapshotId: null);
    }

    private sealed class StaticCutoverParticipant(
        IReadOnlyList<AuthoringRunItemDefinition> items)
        : IAuthoringCutoverParticipant
    {
        public Task<AuthoringCutoverPreparation> PrepareCutoverAsync(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            CancellationToken ct) =>
            Task.FromResult(new AuthoringCutoverPreparation(items));
    }
}

internal sealed class JiraAuthoringTestFixture : IDisposable
{
    private readonly string _directory;

    public JiraAuthoringTestFixture(int maxActiveAuthoringRuns = int.MaxValue)
    {
        _directory = Path.Combine(Path.GetTempPath(), $"fhir-augury-jira-authoring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory, "jira-processing.db");
        SourceStore = new JiraProcessingSourceTicketStore(
            DatabasePath,
            new ResolvedJiraProcessingFilters
            {
                TicketStatuses = ["Triaged"],
                SourceTicketShape = "fhir",
            });
        JiraProcessingDatabase database = new(
            DatabasePath,
            NullLogger<JiraProcessingDatabase>.Instance);
        database.Initialize();
        AuthoringStore = new AuthoringRunStore(database);
        Options = Microsoft.Extensions.Options.Options.Create(new JiraProcessingOptions
        {
            AgentCliCommand = "agent {ticketKey}",
            AuthoringAgentCliCommand = "agent {ticketKey}",
            JiraSourceAddress = "http://source",
            SourceTicketShape = "fhir",
            TicketStatusesToProcess = ["Triaged"],
        });
        Coordinator = new JiraAuthoringRunCoordinator(
            AuthoringStore,
            SourceStore,
            new JiraProcessingFilterResolver(),
            Options,
            Microsoft.Extensions.Options.Options.Create(
                new ProcessingServiceOptions
                {
                    MaxActiveAuthoringRuns = maxActiveAuthoringRuns,
                }));
    }

    public string DatabasePath { get; }
    public JiraProcessingSourceTicketStore SourceStore { get; }
    public AuthoringRunStore AuthoringStore { get; }
    public IOptions<JiraProcessingOptions> Options { get; }
    public JiraAuthoringRunCoordinator Coordinator { get; }

    public Microsoft.Data.Sqlite.SqliteConnection SourceStoreConnection()
    {
        Microsoft.Data.Sqlite.SqliteConnection connection = new(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
        connection.Open();
        return connection;
    }

    public async Task ActivateAsync()
    {
        await AuthoringStore.EnsureProcessorModeAsync(Coordinator.ProcessorKind);
        AuthoringProcessorModeRecord mode =
            await AuthoringStore.GetProcessorModeAsync(Coordinator.ProcessorKind);
        if (mode.Mode == AuthoringStatusValues.ProcessorModes.Legacy)
        {
            await AuthoringStore.TransitionProcessorModeAsync(
                Coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.Legacy,
                AuthoringStatusValues.ProcessorModes.CuttingOver);
            await AuthoringStore.TransitionProcessorModeAsync(
                Coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.CuttingOver,
                AuthoringStatusValues.ProcessorModes.RunBacked);
        }
    }

    public Task<JiraProcessingSourceTicketRecord> SeedAsync(
        string key,
        DateTimeOffset revision,
        string title = "Title")
        => SourceStore.UpsertAsync(
            new JiraIssueSummaryEntry
            {
                Key = key,
                ProjectKey = "FHIR",
                Title = title,
                Type = "Change Request",
                Status = "Triaged",
                WorkGroup = "FHIR-I",
                Specification = "FHIR Core",
                UpdatedAt = revision,
            },
            "fhir",
            resetProcessingStatus: false,
            CancellationToken.None);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
