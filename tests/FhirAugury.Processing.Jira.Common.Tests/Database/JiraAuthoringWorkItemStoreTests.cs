using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Tests.Authoring;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processing.Jira.Common.Tests.Database;

public sealed class JiraAuthoringWorkItemStoreTests
{
    [Fact]
    public async Task SchemaMigrationRefusesMixedCaseLegacyShapesWithoutMutation()
    {
        using AuthoringFixtureScope scope = new();
        JiraAuthoringTestFixture fixture = scope.Fixture;
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

        object?[][] rows = ReadTypedRows(connection, "SELECT * FROM jira_processing_source_tickets ORDER BY RowId");
        object?[][] schema = ReadTypedRows(connection, "SELECT type, name, tbl_name, rootpage, sql FROM sqlite_schema ORDER BY type, name");
        object?[][] index = ReadTypedRows(connection, "PRAGMA index_xinfo(idx_jira_processing_source_tickets_key_shape)");
        object?[][] version = ReadTypedRows(connection, "PRAGMA schema_version");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                () => JiraProcessingSourceTicketStore.EnsureCompositeUniqueIndex(connection));
            Assert.Contains("identity collision", failure.Message);
            Assert.Contains(fixture.DatabasePath, failure.Message);
            Assert.DoesNotContain("FHIR-1", failure.Message);
            Assert.Throws<InvalidOperationException>(
                () => JiraProcessingSourceTicketStore.EnsureSchema(connection));
            Assert.Throws<InvalidOperationException>(
                () => new JiraProcessingSourceTicketStore(fixture.DatabasePath));

            AssertTypedRowsEqual(rows, ReadTypedRows(connection, "SELECT * FROM jira_processing_source_tickets ORDER BY RowId"));
            AssertTypedRowsEqual(schema, ReadTypedRows(connection, "SELECT type, name, tbl_name, rootpage, sql FROM sqlite_schema ORDER BY type, name"));
            AssertTypedRowsEqual(index, ReadTypedRows(connection, "PRAGMA index_xinfo(idx_jira_processing_source_tickets_key_shape)"));
            AssertTypedRowsEqual(version, ReadTypedRows(connection, "PRAGMA schema_version"));
            Assert.Equal(1, SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle));
        }
        connection.Close();
        using (new FileStream(fixture.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewCanonicalKey_FreezesPersistedAuthoringRevision(bool timestamped)
    {
        using AuthoringFixtureScope scope = new();
        JiraAuthoringTestFixture fixture = scope.Fixture;
        await fixture.ActivateAsync();
        JiraIssueSummaryEntry input = new()
        {
            Key = "fHiR-91",
            ProjectKey = "FHIR",
            Title = "Canonical authoring input",
            Status = "Triaged",
            WorkGroup = "FHIR-I",
            Type = "Change Request",
            Specification = "FHIR Core",
            UpdatedAt = timestamped ? new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero) : null,
        };
        JiraProcessingSourceTicketRecord inserted = await fixture.SourceStore.UpsertAsync(
            input, " FHIR ", false, CancellationToken.None);
        Assert.Equal("FHIR-91", inserted.Key);
        string persistedRevision = JiraProcessingSourceTicketStore.GetSourceRevision(inserted);
        string rawRevision = JiraSourceRevision.Compute(inserted with { Key = input.Key });
        Assert.Equal(timestamped, JiraSourceRevision.AreEquivalent(persistedRevision, rawRevision));

        JiraAuthoringRunCreation creation = await fixture.Coordinator.CreateOneItemRunAsync(inserted);
        AuthoringRunItemRecord frozen = Assert.Single(creation.Items);
        Assert.Equal(inserted.Key, frozen.BusinessKey);
        Assert.Equal("fhir", frozen.ItemKind);
        Assert.Equal(persistedRevision, frozen.ExpectedSourceRevision);
        JiraProcessingSourceTicketRecord stored = Assert.IsType<JiraProcessingSourceTicketRecord>(
            await fixture.SourceStore.GetByIdAsync(inserted.Id, CancellationToken.None));

        JiraProcessingSourceTicketStore reopened = new(fixture.DatabasePath);
        _ = new JiraProcessingSourceTicketStore(fixture.DatabasePath);
        AuthoringRunStore reopenedAuthoring = new(fixture.SourceStoreConnection);
        Assert.Equal(frozen, Assert.Single(await reopenedAuthoring.GetRunItemsAsync(creation.Run.Id)));
        Assert.Equal(stored, await reopened.GetByIdAsync(stored.Id, CancellationToken.None));
        using (SqliteConnection connection = fixture.SourceStoreConnection())
        {
            object?[][] before = ReadTypedRows(connection, "SELECT * FROM authoring_run_items ORDER BY RowId");
            await JiraProcessingSourceTicketStore.EnsureCurrentSourceRevisionAsync(
                connection, input.Key, "FHIR", frozen.ExpectedSourceRevision, CancellationToken.None);
            if (!timestamped)
            {
                AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
                    () => JiraProcessingSourceTicketStore.EnsureCurrentSourceRevisionAsync(
                        connection, input.Key, "fhir", rawRevision, CancellationToken.None));
                Assert.Equal(AuthoringConflictCode.SourceRevisionMismatch, conflict.Code);
            }
            AssertTypedRowsEqual(before, ReadTypedRows(connection, "SELECT * FROM authoring_run_items ORDER BY RowId"));
        }

        Assert.True(await reopenedAuthoring.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind, creation.Run.Id));
        JiraAuthoringWorkItemStore workItems = new(reopenedAuthoring, reopened);
        JiraAuthoringWorkItem item = Assert.Single(
            await workItems.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));
        Assert.Equal(stored, item.SourceTicket);
        AuthoringQueueClaim claim = Assert.IsType<AuthoringQueueClaim>(
            await workItems.TryClaimAsync(item, DateTimeOffset.UtcNow, CancellationToken.None));
        AuthoringResultSubmission submission = new(
            creation.Run.Id, item.RunItem.Id, claim.OperationId,
            persistedRevision, AuthoringResultHasher.HashNormalizedUtf8("canonical payload"));
        if (!timestamped)
        {
            using SqliteConnection connection = fixture.SourceStoreConnection();
            IReadOnlyDictionary<string, object?[][]> before = ReadProtectedTables(connection);
            AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
                () => reopenedAuthoring.AcceptResultAsync(
                    submission with { ObservedSourceRevision = rawRevision }, claim.OperationToken));
            Assert.Equal(AuthoringConflictCode.SourceRevisionMismatch, conflict.Code);
            AssertProtectedTablesEqual(before, ReadProtectedTables(connection));
        }
        AuthoringReceiptAcceptance accepted = await reopenedAuthoring.AcceptResultAsync(
            submission, claim.OperationToken);
        await workItems.ApplyResultAsync(
            item, claim, AuthoringWorkResult.Complete(accepted.Receipt.ReceiptId),
            DateTimeOffset.UtcNow, CancellationToken.None);

        JiraProcessingSourceTicketRecord repeated = await reopened.UpsertAsync(
            input with { Key = "fhIR-91" }, "FhIr", false, CancellationToken.None);
        Assert.Equal(stored.Id, repeated.Id);
        Assert.Equal(stored.RowId, repeated.RowId);
        Assert.Equal(stored.Key, repeated.Key);
        Assert.Equal(persistedRevision, JiraSourceRevision.Compute(repeated));
        AuthoringRunItemRecord completed = Assert.Single(
            await reopenedAuthoring.GetRunItemsAsync(creation.Run.Id));
        Assert.Equal(frozen.BusinessKey, completed.BusinessKey);
        Assert.Equal(frozen.ExpectedSourceRevision, completed.ExpectedSourceRevision);
        Assert.Equal(accepted.Receipt.ReceiptId, completed.AcceptedReceiptId);
        Assert.Equal(AuthoringStatusValues.Items.Complete, completed.Status);
        using (SqliteConnection connection = fixture.SourceStoreConnection())
        {
            IReadOnlyDictionary<string, object?[][]> before = ReadProtectedTables(connection);
            _ = new JiraProcessingSourceTicketStore(fixture.DatabasePath);
            await JiraProcessingSourceTicketStore.EnsureCurrentSourceRevisionAsync(
                connection, repeated.Key, repeated.SourceTicketShape,
                completed.ExpectedSourceRevision, CancellationToken.None);
            AssertProtectedTablesEqual(before, ReadProtectedTables(connection));
        }
    }

    [Fact]
    public async Task ClaimAndReceiptDrivenCompletionUseRunItemState()
    {
        using AuthoringFixtureScope scope = new();
        JiraAuthoringTestFixture fixture = scope.Fixture;
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
        using AuthoringFixtureScope scope = new();
        JiraAuthoringTestFixture fixture = scope.Fixture;
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
        using AuthoringFixtureScope scope = new();
        JiraAuthoringTestFixture fixture = scope.Fixture;
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
        using AuthoringFixtureScope scope = new();
        JiraAuthoringTestFixture fixture = scope.Fixture;
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
    public async Task PersistedReceiptResume_DoesNotConsultLabelMatcher()
    {
        using AuthoringFixtureScope scope = new();
        JiraAuthoringTestFixture fixture = scope.Fixture;
        await fixture.ActivateAsync();
        fixture.Options.Value.LabelsToInclude = ["cohort"];
        await fixture.SeedAsync(
            "FHIR-1", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        fixture.Matcher.Enqueue(["FHIR-1"]);
        JiraAuthoringRunCreation creation = Assert.IsType<JiraAuthoringRunCreation>(
            await fixture.Coordinator.CreateScheduledRunAsync());
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind, creation.Run.Id));
        JiraAuthoringWorkItemStore store = new(fixture.AuthoringStore, fixture.SourceStore);
        JiraAuthoringWorkItem item = Assert.Single(
            await store.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));
        DateTimeOffset startedAt = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringQueueClaim authoringClaim = Assert.IsType<AuthoringQueueClaim>(
            await store.TryClaimAsync(item, startedAt, CancellationToken.None));
        AuthoringReceiptAcceptance receipt = await fixture.AuthoringStore.AcceptResultAsync(
            new AuthoringResultSubmission(
                creation.Run.Id,
                item.RunItem.Id,
                authoringClaim.OperationId,
                item.RunItem.ExpectedSourceRevision,
                AuthoringResultHasher.HashNormalizedUtf8("payload")),
            authoringClaim.OperationToken,
            now: startedAt.AddSeconds(1));
        fixture.Options.Value.LabelsToInclude = ["different-cohort"];
        fixture.Options.Value.LabelsToExclude = ["cohort"];

        JiraAuthoringWorkItem persisted = Assert.Single(
            await store.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));
        AuthoringQueueClaim firstLease = Assert.IsType<AuthoringQueueClaim>(
            await store.TryClaimAsync(persisted, startedAt.AddSeconds(2), CancellationToken.None));
        await store.ApplyResultAsync(
            persisted,
            firstLease,
            AuthoringWorkResult.Retry("Post-persistence work failed."),
            startedAt.AddSeconds(3),
            CancellationToken.None);
        Assert.Equal(1, (await fixture.AuthoringStore.ReconcileErroredItemsAsync(
            creation.Run.Id, startedAt.AddMinutes(2))).ResumedReceiptItems);
        JiraAuthoringWorkItem resumed = Assert.Single(
            await store.GetPendingAsync(creation.Run.Id, 10, CancellationToken.None));
        AuthoringQueueClaim retryLease = Assert.IsType<AuthoringQueueClaim>(
            await store.TryClaimAsync(resumed, startedAt.AddMinutes(2), CancellationToken.None));
        await store.ApplyResultAsync(
            resumed,
            retryLease,
            AuthoringWorkResult.Complete(receipt.Receipt.ReceiptId),
            startedAt.AddMinutes(2).AddSeconds(1),
            CancellationToken.None);

        AuthoringRunItemRecord completed = Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(creation.Run.Id));
        Assert.Equal(AuthoringStatusValues.Items.Complete, completed.Status);
        Assert.Equal(receipt.Receipt.ReceiptId, completed.AcceptedReceiptId);
        Assert.Equal(authoringClaim.OperationId, completed.CurrentOperationId);
        Assert.Equal(item.RunItem.ExpectedSourceRevision, completed.ExpectedSourceRevision);
        Assert.Equal(1, completed.AttemptCount);
        Assert.Equal(1, retryLease.AttemptNumber);
        Assert.Empty(retryLease.OperationToken);
        Assert.NotEqual(firstLease.OperationId, retryLease.OperationId);
        Assert.Single(fixture.Matcher.Calls);
        Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_attempts"));
    }

    [Fact]
    public async Task LateOldClaimCannotFailReplacementOperation()
    {
        using AuthoringFixtureScope scope = new();
        JiraAuthoringTestFixture fixture = scope.Fixture;
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
        using AuthoringFixtureScope scope = new();
        JiraAuthoringTestFixture fixture = scope.Fixture;
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

    private static IReadOnlyDictionary<string, object?[][]> ReadProtectedTables(SqliteConnection connection)
    {
        Dictionary<string, object?[][]> tables = [];
        foreach (object?[] row in ReadTypedRows(
                     connection,
                     "SELECT name FROM sqlite_schema WHERE type = 'table' AND (name LIKE 'authoring_%' OR name = 'jira_processing_source_tickets') ORDER BY name"))
        {
            string name = Assert.IsType<string>(row[0]);
            tables.Add(name, ReadTypedRows(connection, $"SELECT * FROM \"{name.Replace("\"", "\"\"")}\" ORDER BY 1"));
        }
        return tables;
    }

    private static void AssertProtectedTablesEqual(
        IReadOnlyDictionary<string, object?[][]> expected,
        IReadOnlyDictionary<string, object?[][]> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach ((string table, object?[][] rows) in expected)
        {
            AssertTypedRowsEqual(rows, actual[table]);
        }
    }

    private static object?[][] ReadTypedRows(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        List<object?[]> rows = [];
        while (reader.Read())
        {
            rows.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
        }
        return rows.ToArray();
    }

    private static void AssertTypedRowsEqual(object?[][] expected, object?[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int row = 0; row < expected.Length; row++)
        {
            Assert.Equal(expected[row].Length, actual[row].Length);
            for (int column = 0; column < expected[row].Length; column++)
            {
                Assert.Equal(expected[row][column]?.GetType(), actual[row][column]?.GetType());
                Assert.Equal(expected[row][column], actual[row][column]);
            }
        }
    }

    private sealed class AuthoringFixtureScope : IDisposable
    {
        public JiraAuthoringTestFixture Fixture { get; } = new();

        // Connections are operation-scoped and non-pooled; clean only this
        // fixture directory, not the shared fixture's process-global pools.
        public void Dispose() => Directory.Delete(
            Path.GetDirectoryName(Fixture.DatabasePath)
                ?? throw new InvalidOperationException("The fixture has no parent directory."),
            recursive: true);
    }
}
