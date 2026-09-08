using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Tests.Authoring;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processing.Jira.Common.Tests.Database;

public sealed class JiraAuthoringWorkItemStoreTests
{
    [Fact]
    public async Task SchemaMigrationMergesMixedCaseLegacyShapes()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        using SqliteConnection connection = fixture.SourceStoreConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            DROP INDEX idx_jira_processing_source_tickets_key_shape;
            CREATE UNIQUE INDEX idx_jira_processing_source_tickets_key_shape
            ON jira_processing_source_tickets(Key, SourceTicketShape);
            UPDATE jira_processing_source_tickets
            SET SourceTicketShape = 'FHIR'
            WHERE Key = 'FHIR-1';
            INSERT INTO jira_processing_source_tickets(
                Id, Key, Title, Description, Project, Status, WorkGroup, Type, Specification,
                SourceTicketShape, LastSyncedAt, LastUpdated, StartedProcessingAt,
                CompletedProcessingAt, LastProcessingAttemptAt, ProcessingStatus,
                ProcessingError, ProcessingAttemptCount, CompletionId, ErrorMessage,
                AgentExitCode, ErrorOccurredAt)
            SELECT
                @id, Key, 'Newest', Description, Project, Status, WorkGroup, Type, Specification,
                'fhir', LastSyncedAt, @lastUpdated, StartedProcessingAt,
                CompletedProcessingAt, LastProcessingAttemptAt, ProcessingStatus,
                ProcessingError, ProcessingAttemptCount, CompletionId, ErrorMessage,
                AgentExitCode, ErrorOccurredAt
            FROM jira_processing_source_tickets
            WHERE Key = 'FHIR-1';
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@lastUpdated", "2026-09-02T00:00:00.0000000+00:00");
        command.ExecuteNonQuery();

        JiraProcessingSourceTicketStore.EnsureCompositeUniqueIndex(connection);

        command.Parameters.Clear();
        command.CommandText =
            "SELECT COUNT(*), MIN(SourceTicketShape), MAX(Title) FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("fhir", reader.GetString(1));
        Assert.Equal("Newest", reader.GetString(2));
    }

    [Fact]
    public async Task ClaimAndReceiptDrivenCompletionUseRunItemState()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        JiraAuthoringRunCreation creation =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        JiraAuthoringWorkItemStore store = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem item = Assert.Single(
            await store.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));

        AuthoringQueueClaim claim = Assert.IsType<AuthoringQueueClaim>(
            await store.TryClaimAsync(item, DateTimeOffset.UtcNow, CancellationToken.None));
        AuthoringReceiptAcceptance accepted = await fixture.AuthoringStore.AcceptResultAsync(
            new AuthoringResultSubmission(
                creation.Run.Id,
                item.RunItem.Id,
                claim.OperationId,
                item.RunItem.ExpectedSourceRevision,
                AuthoringResultHasher.HashNormalizedUtf8("payload")),
            claim.OperationToken);
        await store.ApplyResultAsync(
            item,
            claim,
            AuthoringWorkResult.Complete(accepted.Receipt.ReceiptId),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        AuthoringRunItemRecord completed = Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(creation.Run.Id));
        Assert.Equal(AuthoringStatusValues.Items.Complete, completed.Status);
        Assert.Equal(accepted.Receipt.ReceiptId, completed.AcceptedReceiptId);
    }

    [Fact]
    public async Task ResetOrphanedItemsSupersedesClaimAndAllowsNewOperation()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        JiraAuthoringRunCreation creation =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        JiraAuthoringWorkItemStore store = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem item = Assert.Single(
            await store.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));
        DateTimeOffset startedAt = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        AuthoringQueueClaim first = Assert.IsType<AuthoringQueueClaim>(
            await store.TryClaimAsync(item, startedAt, CancellationToken.None));

        AuthoringOrphanRecoveryResult reset =
            await store.ResetOrphanedItemsAsync(
            creation.Run.Id,
            TimeSpan.FromMinutes(10),
            startedAt.AddMinutes(11),
            CancellationToken.None);
        await fixture.AuthoringStore.ReconcileErroredItemsAsync(
            creation.Run.Id,
            startedAt.AddMinutes(12));
        JiraAuthoringWorkItem retried = Assert.Single(
            await store.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));
        AuthoringQueueClaim second = Assert.IsType<AuthoringQueueClaim>(
            await store.TryClaimAsync(retried, startedAt.AddMinutes(12), CancellationToken.None));

        Assert.Equal(1, reset.RecoveredItems);
        Assert.Null(reset.NextRecoveryAt);
        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(2, second.AttemptNumber);
    }

    [Fact]
    public async Task ResetOrphanedItems_UsesPostPersistenceLeaseAcquiredAt()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        JiraAuthoringRunCreation creation =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        JiraAuthoringWorkItemStore store = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem authoringItem = Assert.Single(
            await store.GetPendingAsync(
                creation.Run.Id,
                10,
                CancellationToken.None));
        DateTimeOffset authoringStartedAt =
            new(2026, 9, 3, 11, 0, 0, TimeSpan.Zero);
        AuthoringQueueClaim authoringClaim =
            Assert.IsType<AuthoringQueueClaim>(
                await store.TryClaimAsync(
                    authoringItem,
                    authoringStartedAt,
                    CancellationToken.None));
        AuthoringReceiptAcceptance receipt =
            await fixture.AuthoringStore.AcceptResultAsync(
                new AuthoringResultSubmission(
                    creation.Run.Id,
                    authoringItem.RunItem.Id,
                    authoringClaim.OperationId,
                    authoringItem.RunItem.ExpectedSourceRevision,
                    AuthoringResultHasher.HashNormalizedUtf8("payload")),
                authoringClaim.OperationToken,
                now: authoringStartedAt.AddMinutes(1));
        JiraAuthoringWorkItem persistedItem = Assert.Single(
            await store.GetPendingAsync(
                creation.Run.Id,
                10,
                CancellationToken.None));
        DateTimeOffset leaseStartedAt = authoringStartedAt.AddHours(1);
        _ = Assert.IsType<AuthoringQueueClaim>(
            await store.TryClaimAsync(
                persistedItem,
                leaseStartedAt,
                CancellationToken.None));

        AuthoringOrphanRecoveryResult early =
            await store.ResetOrphanedItemsAsync(
                creation.Run.Id,
                TimeSpan.FromMinutes(10),
                leaseStartedAt.AddMinutes(5),
                CancellationToken.None);

        Assert.Equal(0, early.RecoveredItems);
        Assert.Equal(leaseStartedAt.AddMinutes(10), early.NextRecoveryAt);
        Assert.Equal(
            AuthoringStatusValues.Items.InProgress,
            Assert.Single(
                await fixture.AuthoringStore.GetRunItemsAsync(
                    creation.Run.Id)).Status);

        AuthoringOrphanRecoveryResult due =
            await store.ResetOrphanedItemsAsync(
                creation.Run.Id,
                TimeSpan.FromMinutes(10),
                leaseStartedAt.AddMinutes(10),
                CancellationToken.None);
        AuthoringRunItemRecord recovered = Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(creation.Run.Id));

        Assert.Equal(1, due.RecoveredItems);
        Assert.Null(due.NextRecoveryAt);
        Assert.Equal(AuthoringStatusValues.Items.Persisted, recovered.Status);
        Assert.Equal(receipt.Receipt.ReceiptId, recovered.AcceptedReceiptId);
        Assert.Equal(1, recovered.AttemptCount);
    }

    [Fact]
    public async Task PersistedReceiptResumesWithoutRelaunchingAuthoring()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        JiraAuthoringRunCreation creation =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        JiraAuthoringWorkItemStore store = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem authoringItem = Assert.Single(
            await store.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));
        AuthoringQueueClaim authoringClaim = Assert.IsType<AuthoringQueueClaim>(
            await store.TryClaimAsync(authoringItem, DateTimeOffset.UtcNow, CancellationToken.None));
        AuthoringReceiptAcceptance accepted = await fixture.AuthoringStore.AcceptResultAsync(
            new AuthoringResultSubmission(
                creation.Run.Id,
                authoringItem.RunItem.Id,
                authoringClaim.OperationId,
                authoringItem.RunItem.ExpectedSourceRevision,
                AuthoringResultHasher.HashNormalizedUtf8("payload")),
            authoringClaim.OperationToken);

        JiraAuthoringWorkItem persistedItem = Assert.Single(
            await store.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));
        AuthoringQueueClaim postReceiptClaim = Assert.IsType<AuthoringQueueClaim>(
            await store.TryClaimAsync(persistedItem, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.NotEqual(authoringClaim.OperationId, postReceiptClaim.OperationId);
        Assert.Empty(postReceiptClaim.OperationToken);
        await store.ApplyResultAsync(
            persistedItem,
            postReceiptClaim,
            AuthoringWorkResult.Complete(accepted.Receipt.ReceiptId),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(
            AuthoringStatusValues.Items.Complete,
            Assert.Single(await fixture.AuthoringStore.GetRunItemsAsync(creation.Run.Id)).Status);
    }

    [Fact]
    public async Task LateOldClaimCannotFailReplacementOperation()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        JiraAuthoringRunCreation creation =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        JiraAuthoringWorkItemStore store = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem item = Assert.Single(
            await store.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));
        DateTimeOffset startedAt = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        AuthoringQueueClaim first = Assert.IsType<AuthoringQueueClaim>(
            await store.TryClaimAsync(item, startedAt, CancellationToken.None));
        await store.ResetOrphanedItemsAsync(
            creation.Run.Id,
            TimeSpan.FromMinutes(10),
            startedAt.AddMinutes(11),
            CancellationToken.None);
        await fixture.AuthoringStore.ReconcileErroredItemsAsync(
            creation.Run.Id,
            startedAt.AddMinutes(12));
        JiraAuthoringWorkItem replacementItem = Assert.Single(
            await store.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));
        AuthoringQueueClaim replacement = Assert.IsType<AuthoringQueueClaim>(
            await store.TryClaimAsync(
                replacementItem,
                startedAt.AddMinutes(12),
                CancellationToken.None));

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => store.ApplyResultAsync(
                item,
                first,
                AuthoringWorkResult.Retry("late failure"),
                startedAt.AddMinutes(13),
                CancellationToken.None));
        AuthoringRunItemRecord current = Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(creation.Run.Id));

        Assert.Equal(AuthoringConflictCode.StaleOperation, conflict.Code);
        Assert.Equal(replacement.OperationId, current.CurrentOperationId);
        Assert.Equal(AuthoringStatusValues.Items.InProgress, current.Status);
    }

    [Fact]
    public async Task LatePostPersistenceLeaseCannotCompleteReplacementLease()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        JiraAuthoringRunCreation creation =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        JiraAuthoringWorkItemStore store = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem authoringItem = Assert.Single(
            await store.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));
        AuthoringQueueClaim authoringClaim = Assert.IsType<AuthoringQueueClaim>(
            await store.TryClaimAsync(authoringItem, DateTimeOffset.UtcNow, CancellationToken.None));
        AuthoringReceiptAcceptance accepted = await fixture.AuthoringStore.AcceptResultAsync(
            new AuthoringResultSubmission(
                creation.Run.Id,
                authoringItem.RunItem.Id,
                authoringClaim.OperationId,
                authoringItem.RunItem.ExpectedSourceRevision,
                AuthoringResultHasher.HashNormalizedUtf8("payload")),
            authoringClaim.OperationToken);
        DateTimeOffset startedAt = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        JiraAuthoringWorkItem firstPostItem = Assert.Single(
            await store.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));
        AuthoringQueueClaim firstPostLease = Assert.IsType<AuthoringQueueClaim>(
            await store.TryClaimAsync(firstPostItem, startedAt, CancellationToken.None));
        await store.ResetOrphanedItemsAsync(
            creation.Run.Id,
            TimeSpan.FromMinutes(10),
            startedAt.AddMinutes(11),
            CancellationToken.None);
        JiraAuthoringWorkItem replacementItem = Assert.Single(
            await store.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));
        AuthoringQueueClaim replacementLease = Assert.IsType<AuthoringQueueClaim>(
            await store.TryClaimAsync(
                replacementItem,
                startedAt.AddMinutes(11),
                CancellationToken.None));

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => store.ApplyResultAsync(
                firstPostItem,
                firstPostLease,
                AuthoringWorkResult.Complete(accepted.Receipt.ReceiptId),
                startedAt.AddMinutes(12),
                CancellationToken.None));
        AuthoringRunItemRecord current = Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(creation.Run.Id));

        Assert.Equal(AuthoringConflictCode.StaleOperation, conflict.Code);
        Assert.Equal(replacementLease.OperationId, current.PostPersistenceLeaseId);
        Assert.Equal(AuthoringStatusValues.Items.InProgress, current.Status);
    }
}
