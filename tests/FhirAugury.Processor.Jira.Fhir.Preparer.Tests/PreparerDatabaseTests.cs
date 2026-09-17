using FhirAugury.Common.Api;
using FhirAugury.Common.Text;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparerDatabaseTests
{
    [Fact]
    public void Initialize_CreatesPreparedTicketTablesAndIndexes()
    {
        using TestDatabase database = CreateDatabase();

        Assert.True(Exists(database, "table", "prepared_tickets"));
        Assert.True(Exists(database, "table", "prepared_ticket_repos"));
        Assert.True(Exists(database, "table", "prepared_ticket_related_jira"));
        Assert.True(Exists(database, "table", "prepared_ticket_related_zulip"));
        Assert.True(Exists(database, "table", "prepared_ticket_related_github"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_tickets"));
        Assert.True(HasUniqueIndexOver(database, "prepared_tickets", "Key"));
        Assert.True(HasUniqueIndexOver(database, "prepared_tickets", "Id"));
        Assert.True(Exists(database, "table", "prepared_ticket_jira_content"));
        Assert.True(Exists(database, "table", "prepared_ticket_artifacts"));
        Assert.True(Exists(database, "table", "prepared_ticket_pages"));
        Assert.True(Exists(
            database,
            "table",
            "prepared_ticket_in_person_requesters"));
        Assert.True(Exists(database, "table", "prepared_ticket_authoring_state"));
        Assert.True(Exists(database, "table", "prepared_ticket_partition_receipts"));
        Assert.True(Exists(
            database,
            "table",
            "prepared_ticket_publication_refresh_receipts"));
        Assert.True(Exists(
            database,
            "table",
            "prepared_ticket_partition_certifications"));
        Assert.True(HasUniqueIndexOverColumns(
            database,
            "prepared_ticket_publication_refresh_receipts",
            ["RunId", "StageId"]));
        Assert.True(HasUniqueIndexOverColumns(
            database,
            "prepared_ticket_partition_certifications",
            ["RunId", "PartitionKey"]));
        foreach (string table in new[]
        {
            "prepared_ticket_publication_reconciliations",
            "prepared_ticket_publication_reconciliation_items",
            "prepared_ticket_publication_staged_graphs",
            "prepared_ticket_publication_staged_hydration",
            "prepared_ticket_publication_staged_receipts",
            "prepared_ticket_publication_grouping_impacts",
            "prepared_ticket_publication_staged_grouping",
            "prepared_ticket_publication_reconciliation_proofs",
            "prepared_ticket_publication_reconciliation_journal",
            "prepared_ticket_publication_reconciliation_fences",
            "prepared_ticket_publication_snapshot_descriptors",
        })
        {
            Assert.True(Exists(database, "table", table), table);
        }
    }

    [Fact]
    public async Task MixedRunCreation_ParticipatesInCallerTransaction()
    {
        using TestDatabase database = CreateDatabase();
        AuthoringRunStore store = new(database.Database);
        await store.EnsureProcessorModeAsync("jira-fhir");
        await store.TransitionProcessorModeAsync(
            "jira-fhir",
            AuthoringStatusValues.ProcessorModes.Legacy,
            AuthoringStatusValues.ProcessorModes.CuttingOver);
        await store.TransitionProcessorModeAsync(
            "jira-fhir",
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            AuthoringStatusValues.ProcessorModes.RunBacked);

        string runId = Guid.NewGuid().ToString("N");
        await using (SqliteConnection connection =
                     database.Database.OpenConnection())
        await using (SqliteTransaction transaction =
                     connection.BeginTransaction(deferred: false))
        {
            await store.CreateMixedRunAsync(
                connection,
                transaction,
                "jira-fhir",
                PreparedTicketPublicationReconciliationContract.Purpose,
                "source-run",
                [
                    new AuthoringMixedRunItem(
                        "FHIR-1",
                        "fhir",
                        "revision-1",
                        AuthoringStatusValues.Items.Pending),
                    new AuthoringMixedRunItem(
                        "FHIR-2",
                        "fhir",
                        "revision-2",
                        AuthoringStatusValues.Items.Complete,
                        "receipt-2"),
                ],
                runId);
            await transaction.RollbackAsync();
        }

        Assert.Null(await store.GetRunAsync(runId));
        Assert.Empty(await store.GetRunItemsAsync(runId));
    }

    [Fact]
    public async Task ReconciliationStaging_IsIdempotentAndOnlyVisibleInOverlay()
    {
        using TestDatabase database = CreateDatabase();
        AuthoringRunStore store = new(database.Database);
        await store.EnsureProcessorModeAsync("jira-fhir");
        await store.TransitionProcessorModeAsync(
            "jira-fhir",
            AuthoringStatusValues.ProcessorModes.Legacy,
            AuthoringStatusValues.ProcessorModes.CuttingOver);
        await store.TransitionProcessorModeAsync(
            "jira-fhir",
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            AuthoringStatusValues.ProcessorModes.RunBacked);
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        PreparedTicketPublicationReconciliationComparison comparison = new(
            PreparedTicketPublicationReconciliationContract.CurrentVersion,
            "source-run",
            "snapshot-1",
            "snapshot-sha",
            "jira-generation-1",
            capturedAt,
            "corpus-fingerprint",
            [
                new PreparedTicketPublicationReconciliationItemDecision(
                    "FHIR-1",
                    PreparedTicketPublicationReconciliationDispositionValues
                        .ReAuthor,
                    "revision-0",
                    "revision-1",
                    "old-receipt",
                    "old-item",
                    "old-run",
                    "old-graph",
                    "old-grouping",
                    "fhir",
                    "revision-1"),
            ]);
        AuthoringRunRecord run =
            await database.Database.CreatePublicationReconciliationAsync(
                comparison,
                capturedAt);
        AuthoringRunItemRecord item = Assert.Single(
            await store.GetRunItemsAsync(run.Id));
        PreparedTicketPayload payload = SamplePayload("FHIR-1");
        PreparedTicketHydrationBatch hydration =
            SampleBatch("FHIR-1", "FHIR-1");

        PreparedTicketPublicationStagedTicket first =
            await database.Database
                .StagePublicationReconciliationTicketAsync(
                    run.Id,
                    item.Id,
                    "operation-1",
                    "receipt-1",
                    "revision-1",
                    "graph-1",
                    payload,
                    hydration,
                    capturedAt);
        PreparedTicketPublicationStagedTicket second =
            await database.Database
                .StagePublicationReconciliationTicketAsync(
                    run.Id,
                    item.Id,
                    "operation-1",
                    "receipt-1",
                    "revision-1",
                    "graph-1",
                    payload,
                    hydration,
                    capturedAt.AddMinutes(1));

        Assert.Equal(first.AuthoredFingerprint, second.AuthoredFingerprint);
        Assert.False(
            await database.Database.PreparedTicketExistsAsync("FHIR-1"));
        PreparedTicketPublicationCorpusOverlay overlay =
            await database.Database.GetPublicationReconciliationCorpusAsync(
                run.Id);
        PreparedTicketPublicationCorpusTicket ticket =
            Assert.Single(overlay.Tickets);
        Assert.Equal("FHIR-1", ticket.TicketKey);
        Assert.Equal("graph-1", ticket.AuthoredFingerprint);
        Assert.Equal("fhir", ticket.ItemKind);
        Assert.Equal("revision-1", ticket.ExpectedSourceRevision);
        Assert.Equal(
            PreparedTicketPublicationContract.ComputeCorpusFingerprint(
                [ticket.ToPublicationCorpusItem()]),
            overlay.CorpusFingerprint);
        Assert.Equal(
            1,
            Count(
                database,
                "prepared_ticket_publication_staged_graphs"));
        Assert.Equal(
            1,
            Count(
                database,
                "prepared_ticket_publication_staged_hydration"));
        Assert.Equal(
            1,
            Count(
                database,
                "prepared_ticket_publication_staged_receipts"));
    }

    [Fact]
    public async Task ClassifyLegacyPreparedTickets_MarksRowsWithoutInventingReceipts()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SavePreparedTicketAsync(SamplePayload("FHIR-123"));

        int classified = await database.Database.ClassifyLegacyPreparedTicketsAsync();

        Assert.Equal(1, classified);
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT Classification FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-123'";
        Assert.Equal("legacy-unverified", command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM authoring_result_receipts";
        Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar()));
        Assert.Equal(1, await database.Database.CountLegacyUnverifiedAsync());
    }

    [Fact]
    public async Task MaintenanceLeaseExcludesAuthoringFenceUntilReleased()
    {
        using TestDatabase database = CreateDatabase();
        AuthoringRunStore store = new(database.Database);
        await store.EnsureProcessorModeAsync("jira-fhir");
        await store.TransitionProcessorModeAsync(
            "jira-fhir",
            AuthoringStatusValues.ProcessorModes.Legacy,
            AuthoringStatusValues.ProcessorModes.CuttingOver);
        await store.TransitionProcessorModeAsync(
            "jira-fhir",
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            AuthoringStatusValues.ProcessorModes.RunBacked);
        AuthoringRunRecord run = await store.CreateRunAsync(
            "jira-fhir",
            [new AuthoringRunItemDefinition("FHIR-1", "fhir", "revision-1")]);
        using (SqliteConnection connection = database.Database.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO authoring_mutation_fences(ProcessorKind, RunId, LeaseId, AcquiredAt)
                VALUES('jira-fhir', 'maintenance:previous-process:test', 'old-lease', @at)
                """;
            command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        Assert.Null(
            await database.Database.TryAcquireMaintenanceLeaseAsync("before-recovery"));
        database.Database.AcquireStartupOwnership();
        Assert.Equal(
            1,
            await database.Database.RecoverInterruptedMaintenanceLeasesAsync());
        PreparerMaintenanceLease lease =
            Assert.IsType<PreparerMaintenanceLease>(
                await database.Database.TryAcquireMaintenanceLeaseAsync("test"));
        using (SqliteConnection connection = database.Database.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE authoring_mutation_fences SET AcquiredAt = '2000-01-01T00:00:00.0000000+00:00' WHERE RunId = @runId";
            command.Parameters.AddWithValue("@runId", lease.RunId);
            command.ExecuteNonQuery();
        }

        Assert.Null(await database.Database.TryAcquireMaintenanceLeaseAsync("second"));
        Assert.False(await store.TryAcquireMutationFenceAsync("jira-fhir", run.Id));
        await database.Database.ReleaseMaintenanceLeaseAsync(lease);
        Assert.True(await store.TryAcquireMutationFenceAsync("jira-fhir", run.Id));
    }

    [Fact]
    public async Task PublicationRefresh_UpdatesOnlyAllowlistedMetadataAndReusesReceipt()
    {
        using TestDatabase database = CreateDatabase();
        PublicationRefreshContext context =
            await CreatePublicationRefreshContextAsync(
                database,
                "FHIR-701");
        string protectedBefore = ReadProtectedPublicationState(database);
        DateTimeOffset refreshAt =
            new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset hydratedAt =
            new(2026, 9, 14, 13, 0, 0, TimeSpan.Zero);
        PreparedTicketPublicationMetadata metadata = new(
            "FHIR-701",
            "revision-FHIR-701",
            "  Current Reporter ",
            "Current Assignee",
            [" Requester B ", "requester b", "Requester A"],
            "fhir",
            refreshAt,
            701,
            true,
            PublicDisplayNamePolicy.CurrentVersion,
            hydratedAt);

        PreparedTicketPublicationRefreshReceiptRecord receipt =
            await database.Database.ApplyPublicationMetadataAsync(
                context.RefreshRunId,
                context.Lease,
                context.InputFingerprint,
                context.Inventory,
                [metadata]);

        Assert.Equal(
            context.Inventory.CorpusFingerprint,
            receipt.CorpusFingerprint);
        Assert.Equal(701, receipt.SourceContentRevision);
        Assert.Equal(refreshAt, receipt.SourceLastSuccessfulRefreshAt);
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            receipt.PublicDisplayNamePolicyVersion);
        Assert.Equal(protectedBefore, ReadProtectedPublicationState(database));

        PreparedTicketHydrationReadModel hydration =
            Assert.IsType<PreparedTicketHydrationReadModel>(
                await database.Database.GetHydrationAsync("FHIR-701"));
        Assert.Equal("Current Reporter", hydration.Parent!.Reporter);
        Assert.Equal("Current Assignee", hydration.Parent.Assignee);
        Assert.Equal("FHIR", hydration.Parent.SourceProject);
        Assert.Equal(refreshAt, hydration.Parent.SourceLastSuccessfulRefreshAt);
        Assert.Equal(701, hydration.Parent.SourceContentRevision);
        Assert.Equal(hydratedAt, hydration.Parent.HydratedAt);
        Assert.Equal("resolved", hydration.Parent.HydrationStatus);
        Assert.Null(hydration.Parent.HydrationReason);
        Assert.Equal(
            ["Requester A", "Requester B"],
            hydration.InPersonRequesters
                .Select(row => row.DisplayName)
                .ToArray());
        PreparedJiraHydrationRow self = Assert.Single(
            hydration.JiraRows,
            row => row.JiraKey == row.TicketKey);
        Assert.Equal("Current Reporter", self.Reporter);
        Assert.Equal("Current Assignee", self.Assignee);

        AuthoringConflictException fullHydrationBlocked =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => database.Database.SaveHydrationAsync(
                    SampleBatch("FHIR-701", "FHIR-701")));
        Assert.Equal(
            AuthoringConflictCode.MutationFenceUnavailable,
            fullHydrationBlocked.Code);

        using (SqliteConnection connection =
               database.Database.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT LatestSuccessfulRefreshAt, ContentRevision
                FROM authoring_run_input_provenance
                WHERE RunId = @runId AND Source = 'jira'
                """;
            command.Parameters.AddWithValue(
                "@runId",
                context.SourceRunId);
            using SqliteDataReader historical = command.ExecuteReader();
            Assert.True(historical.Read());
            Assert.Equal(
                "2001-01-01T00:00:00.0000000+00:00",
                historical.GetString(0));
            Assert.Equal(1, historical.GetInt64(1));
            Assert.False(historical.Read());
            historical.Close();

            command.Parameters.Clear();
            command.CommandText =
                """
                SELECT LatestSuccessfulRefreshAt, ContentRevision
                FROM authoring_run_input_provenance
                WHERE RunId = @runId AND Source = 'jira'
                """;
            command.Parameters.AddWithValue(
                "@runId",
                context.RefreshRunId);
            using SqliteDataReader refreshed = command.ExecuteReader();
            Assert.True(refreshed.Read());
            Assert.Equal(refreshAt, DateTimeOffset.Parse(refreshed.GetString(0)));
            Assert.Equal(701, refreshed.GetInt64(1));
            Assert.False(refreshed.Read());
        }

        PreparedTicketPublicationMetadata retry = metadata with
        {
            Reporter = "Must Not Replace",
            HydratedAt = hydratedAt.AddHours(1),
        };
        AuthoringRunStageLease reclaimedLease =
            Assert.IsType<AuthoringRunStageLease>(
                await new AuthoringRunStore(database.Database)
                    .TryStartRunStageAsync(
                        context.Lease.StageId,
                        orphanedAfter: TimeSpan.FromSeconds(1),
                        now: DateTimeOffset.UtcNow.AddMinutes(1)));
        PreparedTicketPublicationRefreshReceiptRecord retried =
            await database.Database.ApplyPublicationMetadataAsync(
                context.RefreshRunId,
                reclaimedLease,
                context.InputFingerprint,
                context.Inventory,
                [retry]);
        Assert.Equal(receipt.RowId, retried.RowId);
        hydration = Assert.IsType<PreparedTicketHydrationReadModel>(
            await database.Database.GetHydrationAsync("FHIR-701"));
        Assert.Equal("Current Reporter", hydration.Parent!.Reporter);
        Assert.Equal(hydratedAt, hydration.Parent.HydratedAt);
        Assert.Equal(
            1,
            Count(
                database,
                "prepared_ticket_publication_refresh_receipts"));
    }

    [Fact]
    public async Task PublicationRefresh_AcceptsEquivalentTimestampOffsets()
    {
        const string frozenRevision =
            "2025-07-17T16:12:12.0000000-05:00";
        using TestDatabase database = CreateDatabase();
        PublicationRefreshContext context =
            await CreatePublicationRefreshContextAsync(
                database,
                _ => frozenRevision,
                "FHIR-10028");
        string protectedBefore = ReadProtectedPublicationState(database);
        PreparedTicketPublicationRefreshCandidate frozenCandidate =
            Assert.Single(context.Inventory.Candidates);
        Assert.Equal(
            frozenRevision,
            frozenCandidate.ExpectedSourceRevision);

        PreparedTicketPublicationRefreshReceiptRecord receipt =
            await database.Database.ApplyPublicationMetadataAsync(
                context.RefreshRunId,
                context.Lease,
                context.InputFingerprint,
                context.Inventory,
                [
                    PublicationMetadata(
                        "FHIR-10028",
                        "2025-07-17T21:12:12.0000000+00:00",
                        10028,
                        new DateTimeOffset(
                            2026,
                            9,
                            14,
                            12,
                            0,
                            0,
                            TimeSpan.Zero)),
                ]);

        Assert.Equal(
            context.Inventory.CorpusFingerprint,
            receipt.CorpusFingerprint);
        Assert.Equal(10028, receipt.SourceContentRevision);
        Assert.Equal(protectedBefore, ReadProtectedPublicationState(database));
        PreparedTicketPublicationRefreshInventory current =
            await database.Database.GetPublicationRefreshInventoryAsync();
        Assert.Equal(
            context.Inventory.CorpusFingerprint,
            current.CorpusFingerprint);
        Assert.Equal(
            frozenRevision,
            Assert.Single(current.Candidates).ExpectedSourceRevision);
        Assert.Equal(
            1,
            Count(
                database,
                "prepared_ticket_publication_refresh_receipts"));
    }

    [Fact]
    public async Task PublicationRefresh_RejectsChangedOrMixedSourceWithoutMutation()
    {
        const string frozenRevision =
            "2025-07-17T16:12:12.0000000-05:00";
        const string equivalentObservedRevision =
            "2025-07-17T21:12:12.0000000+00:00";
        using TestDatabase database = CreateDatabase();
        PublicationRefreshContext context =
            await CreatePublicationRefreshContextAsync(
                database,
                _ => frozenRevision,
                "FHIR-702",
                "FHIR-703");
        string protectedBefore = ReadProtectedPublicationState(database);
        string publicationBefore = ReadPublicationFields(database);
        DateTimeOffset refreshAt =
            new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

        AuthoringConflictException changed =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => database.Database.ApplyPublicationMetadataAsync(
                    context.RefreshRunId,
                    context.Lease,
                    context.InputFingerprint,
                    context.Inventory,
                    [
                        PublicationMetadata(
                            "FHIR-702",
                            "2025-07-17T21:12:13.0000000+00:00",
                            702,
                            refreshAt),
                        PublicationMetadata(
                            "FHIR-703",
                            equivalentObservedRevision,
                            702,
                            refreshAt),
                    ]));
        Assert.Equal(
            AuthoringConflictCode.SourceRevisionMismatch,
            changed.Code);
        Assert.Equal(protectedBefore, ReadProtectedPublicationState(database));
        Assert.Equal(publicationBefore, ReadPublicationFields(database));

        AuthoringConflictException mixed =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => database.Database.ApplyPublicationMetadataAsync(
                    context.RefreshRunId,
                    context.Lease,
                    context.InputFingerprint,
                    context.Inventory,
                    [
                        PublicationMetadata(
                            "FHIR-702",
                            equivalentObservedRevision,
                            702,
                            refreshAt),
                        PublicationMetadata(
                            "FHIR-703",
                            equivalentObservedRevision,
                            703,
                            refreshAt),
                    ]));
        Assert.Equal(
            AuthoringConflictCode.SourceRevisionMismatch,
            mixed.Code);
        Assert.Equal(protectedBefore, ReadProtectedPublicationState(database));
        Assert.Equal(publicationBefore, ReadPublicationFields(database));
        Assert.Equal(
            0,
            Count(
                database,
                "prepared_ticket_publication_refresh_receipts"));
    }

    [Fact]
    public async Task PublicationRefresh_MaintenanceItemsAndReceiptRecoverFromDurableState()
    {
        using TestDatabase database = CreateDatabase();
        PublicationRefreshContext context =
            await CreatePublicationRefreshContextAsync(
                database,
                "FHIR-704");
        IReadOnlyList<AuthoringMaintenanceRunItem> maintenanceItems;
        await using (SqliteConnection connection =
                     database.Database.OpenConnection())
        {
            maintenanceItems =
                await PreparerDatabase
                    .GetPublicationRefreshMaintenanceItemsAsync(
                        connection);
        }

        AuthoringMaintenanceRunItem maintenanceItem =
            Assert.Single(maintenanceItems);
        PreparedTicketPublicationRefreshCandidate candidate =
            Assert.Single(context.Inventory.Candidates);
        Assert.Equal(candidate.TicketKey, maintenanceItem.BusinessKey);
        Assert.Equal(candidate.ItemKind, maintenanceItem.ItemKind);
        Assert.Equal(
            candidate.ExpectedSourceRevision,
            maintenanceItem.ExpectedSourceRevision);
        Assert.Equal(candidate.ReceiptId, maintenanceItem.ReceiptId);

        PreparedTicketPublicationRefreshReceiptRecord applied =
            await database.Database.ApplyPublicationMetadataAsync(
                context.RefreshRunId,
                context.Lease,
                context.InputFingerprint,
                context.Inventory,
                [
                    PublicationMetadata(
                        "FHIR-704",
                        "revision-FHIR-704",
                        704,
                        new DateTimeOffset(
                            2026,
                            9,
                            14,
                            12,
                            0,
                            0,
                            TimeSpan.Zero)),
                ]);

        using PreparerDatabase reopened = new(
            database.Database.DatabasePath,
            NullLogger<PreparerDatabase>.Instance);
        reopened.Initialize();
        PreparedTicketPublicationRefreshReceiptRecord recovered =
            Assert.IsType<PreparedTicketPublicationRefreshReceiptRecord>(
                await reopened.GetMatchingPublicationRefreshReceiptAsync(
                    context.RefreshRunId,
                    context.Lease.StageId,
                    context.InputFingerprint,
                    context.Inventory.CorpusFingerprint));
        Assert.Equal(applied.RowId, recovered.RowId);
        Assert.Equal(applied.AppliedAt, recovered.AppliedAt);
        Assert.Equal(
            applied.SourceContentRevision,
            recovered.SourceContentRevision);
    }

    [Fact]
    public void Initialize_CreatesGroupingTablesAndIndexes()
    {
        using TestDatabase database = CreateDatabase();

        Assert.True(Exists(database, "table", "prepared_ticket_topics"));
        Assert.True(Exists(database, "table", "prepared_ticket_topic_groups"));
        Assert.True(Exists(database, "table", "prepared_ticket_topic_members"));

        Assert.True(IsRowIdPrimaryKey(database, "prepared_ticket_topics"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_ticket_topic_groups"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_ticket_topic_members"));

        Assert.True(HasUniqueIndexOver(database, "prepared_ticket_topics", "Id"));
        Assert.True(HasUniqueIndexOver(database, "prepared_ticket_topic_groups", "Id"));
        Assert.True(HasUniqueIndexOver(database, "prepared_ticket_topic_members", "Id"));

        Assert.True(HasUniqueIndexOverColumns(
            database,
            "prepared_ticket_topics",
            ["WorkGroupClean", "Specification", "Type", "ShortDescription"]));
        Assert.True(HasUniqueIndexOverColumns(
            database,
            "prepared_ticket_topic_groups",
            ["TopicRowId", "FirstTicketKey"]));
        Assert.True(HasUniqueIndexOverColumns(
            database,
            "prepared_ticket_topic_members",
            ["TopicRowId", "TicketKey"]));
    }

    [Fact]
    public void Initialize_CreatesHydrationTablesAndIndexes()
    {
        using TestDatabase database = CreateDatabase();

        Assert.True(Exists(database, "table", "prepared_ticket_hydration"));
        Assert.True(Exists(database, "table", "prepared_jira_hydration"));
        Assert.True(Exists(database, "table", "prepared_zulip_hydration"));
        Assert.True(Exists(database, "table", "prepared_github_hydration"));
        Assert.True(Exists(database, "table", "prepared_repo_hydration"));
        Assert.True(Exists(database, "table", "prepared_ticket_jira_xref"));
        Assert.True(Exists(
            database,
            "table",
            "prepared_ticket_in_person_requesters"));

        Assert.True(IsRowIdPrimaryKey(database, "prepared_ticket_hydration"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_jira_hydration"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_zulip_hydration"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_github_hydration"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_repo_hydration"));
        Assert.True(IsRowIdPrimaryKey(database, "prepared_ticket_jira_xref"));
        Assert.True(IsRowIdPrimaryKey(
            database,
            "prepared_ticket_in_person_requesters"));

        Assert.True(HasUniqueIndexOver(database, "prepared_ticket_hydration", "TicketKey"));
        Assert.True(HasUniqueIndexOverColumns(database, "prepared_jira_hydration", ["TicketKey", "JiraKey"]));
        Assert.True(HasUniqueIndexOverColumns(database, "prepared_zulip_hydration", ["TicketKey", "ZulipThreadId"]));
        Assert.True(HasUniqueIndexOverColumns(database, "prepared_github_hydration", ["TicketKey", "GitHubItemId"]));
        Assert.True(HasUniqueIndexOverColumns(database, "prepared_repo_hydration", ["TicketKey", "Repo"]));
        Assert.True(HasUniqueIndexOverColumns(database, "prepared_ticket_jira_xref", ["TicketKey", "JiraKey", "Source"]));
        Assert.True(HasUniqueIndexOverColumns(
            database,
            "prepared_ticket_in_person_requesters",
            ["TicketKey", "DisplayName"]));
    }

    [Fact]
    public async Task SavePreparedTicket_InsertsParentAndAllRelatedRows()
    {
        using TestDatabase database = CreateDatabase();
        PreparedTicketPayload payload = SamplePayload("FHIR-123");

        PreparedTicketSaveResult result = await database.Database.SavePreparedTicketAsync(payload);

        Assert.Equal("FHIR-123", result.Key);
        Assert.Equal(1, Count(database, "prepared_tickets"));
        Assert.Equal(1, Count(database, "prepared_ticket_repos"));
        Assert.Equal(1, Count(database, "prepared_ticket_related_jira"));
        Assert.Equal(1, Count(database, "prepared_ticket_related_zulip"));
        Assert.Equal(1, Count(database, "prepared_ticket_related_github"));
    }

    [Fact]
    public async Task SavePreparedTicket_OverwritesExistingParentAndChildrenAtomically()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SavePreparedTicketAsync(SamplePayload("FHIR-123"));
        PreparedTicketPayload replacement = SamplePayload("FHIR-123");
        replacement.Repos = [new PreparedTicketRepoPayload { Repo = "HL7/fhir-ig", RepoCategory = "IG", Justification = "new" }];
        replacement.RelatedJiraTickets = [];

        await database.Database.SavePreparedTicketAsync(replacement);

        Assert.Equal(1, Count(database, "prepared_tickets"));
        Assert.Equal(1, Count(database, "prepared_ticket_repos"));
        Assert.Equal(0, Count(database, "prepared_ticket_related_jira"));
        PreparedTicketDetail? detail = await database.Database.GetPreparedTicketAsync("FHIR-123");
        Assert.Equal("HL7/fhir-ig", detail!.RelatedItems.Repos[0].Repo);
    }

    [Fact]
    public async Task SavePreparedTicket_InvalidImpactDoesNotDeleteExistingRows()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SavePreparedTicketAsync(SamplePayload("FHIR-123"));
        PreparedTicketPayload invalid = SamplePayload("FHIR-123");
        invalid.ProposalAImpact = "bad";

        await Assert.ThrowsAsync<ArgumentException>(() => database.Database.SavePreparedTicketAsync(invalid));

        Assert.Equal(1, Count(database, "prepared_tickets"));
        Assert.Equal(1, Count(database, "prepared_ticket_repos"));
    }

    [Fact]
    public async Task SavePreparedTicket_InvalidRecommendationDoesNotDeleteExistingRows()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SavePreparedTicketAsync(SamplePayload("FHIR-123"));
        PreparedTicketPayload invalid = SamplePayload("FHIR-123");
        invalid.Recommendation = "Z";

        await Assert.ThrowsAsync<ArgumentException>(() => database.Database.SavePreparedTicketAsync(invalid));

        Assert.Equal(1, Count(database, "prepared_tickets"));
        Assert.Equal(1, Count(database, "prepared_ticket_repos"));
    }

    [Fact]
    public async Task ListPreparedTickets_FiltersByRecommendationAndImpact()
    {
        using TestDatabase database = CreateDatabase();
        PreparedTicketPayload first = SamplePayload("FHIR-123");
        first.Recommendation = "A";
        first.ProposalAImpact = "Non-substantive";
        PreparedTicketPayload second = SamplePayload("FHIR-124");
        second.Recommendation = "B";
        second.ProposalAImpact = "Compatible, substantive";
        await database.Database.SavePreparedTicketAsync(first);
        await database.Database.SavePreparedTicketAsync(second);

        IReadOnlyList<PreparedTicketSummary> rows = await database.Database.ListPreparedTicketsAsync(new PreparedTicketQueryFilter(Recommendation: "A", Impact: "Non-substantive"));

        PreparedTicketSummary row = Assert.Single(rows);
        Assert.Equal("FHIR-123", row.Key);
    }

    [Fact]
    public async Task GetPreparedTicket_ReturnsParentAndChildren()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SavePreparedTicketAsync(SamplePayload("FHIR-123"));

        PreparedTicketDetail? detail = await database.Database.GetPreparedTicketAsync("FHIR-123");

        Assert.NotNull(detail);
        Assert.Equal("FHIR-123", detail.Ticket.Key);
        Assert.Single(detail.RelatedItems.Repos);
        Assert.Single(detail.RelatedItems.JiraTickets);
        Assert.Single(detail.RelatedItems.ZulipThreads);
        Assert.Single(detail.RelatedItems.GitHubItems);
    }

    [Fact]
    public async Task SaveHydration_InsertsAllSixTables()
    {
        using TestDatabase database = CreateDatabase();
        PreparedTicketHydrationBatch batch = SampleBatch("FHIR-1");

        await database.Database.SaveHydrationAsync(batch);

        Assert.Equal(1, Count(database, "prepared_ticket_hydration"));
        Assert.Equal(1, Count(database, "prepared_jira_hydration"));
        Assert.Equal(1, Count(database, "prepared_zulip_hydration"));
        Assert.Equal(1, Count(database, "prepared_github_hydration"));
        Assert.Equal(1, Count(database, "prepared_repo_hydration"));
        Assert.Equal(1, Count(database, "prepared_ticket_jira_xref"));
        Assert.Equal(
            2,
            Count(database, "prepared_ticket_in_person_requesters"));

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-1");
        Assert.NotNull(read);
        Assert.NotNull(read!.Parent);
        Assert.Equal("FHIR-1", read.Parent!.TicketKey);
        Assert.Equal("resolved", read.Parent.HydrationStatus);
        Assert.Single(read.JiraRows);
        Assert.Single(read.ZulipRows);
        Assert.Single(read.GitHubRows);
        Assert.Single(read.RepoRows);
        Assert.Single(read.JiraXrefRows);
        Assert.Equal(2, read.InPersonRequesters.Count);
    }

    [Fact]
    public async Task SaveHydration_ReplacesPriorRowsForSameTicket()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SaveHydrationAsync(SampleBatch("FHIR-1", jiraKey: "FHIR-100"));
        await database.Database.SaveHydrationAsync(SampleBatch("FHIR-1", jiraKey: "FHIR-200"));

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-1");

        Assert.NotNull(read);
        Assert.Single(read!.JiraRows);
        Assert.Equal("FHIR-200", read.JiraRows[0].JiraKey);
    }

    [Fact]
    public async Task SaveHydration_NormalizesDeduplicatesAndReplacesRequesters()
    {
        using TestDatabase database = CreateDatabase();
        PreparedTicketHydrationBatch first = SampleBatch("FHIR-1") with
        {
            InPersonRequesters =
            [
                new("FHIR-1", "  Zoë Example ",
                    PublicDisplayNamePolicy.CurrentVersion),
                new("FHIR-1", "zoë example",
                    PublicDisplayNamePolicy.CurrentVersion),
                new("FHIR-1", " ",
                    PublicDisplayNamePolicy.CurrentVersion),
                new("FHIR-1", "Alan Example",
                    PublicDisplayNamePolicy.CurrentVersion),
            ],
        };
        await database.Database.SaveHydrationAsync(first);

        PreparedTicketHydrationReadModel read =
            Assert.IsType<PreparedTicketHydrationReadModel>(
                await database.Database.GetHydrationAsync("FHIR-1"));
        Assert.Equal(
            ["Alan Example", "Zoë Example"],
            read.InPersonRequesters.Select(row => row.DisplayName).ToArray());

        PreparedTicketHydrationBatch replacement = SampleBatch("FHIR-1") with
        {
            InPersonRequesters =
            [
                new("FHIR-1", "Grace Example",
                    PublicDisplayNamePolicy.CurrentVersion),
                new("FHIR-1", "grace example",
                    PublicDisplayNamePolicy.CurrentVersion),
            ],
        };
        await database.Database.SaveHydrationAsync(replacement);

        read = Assert.IsType<PreparedTicketHydrationReadModel>(
            await database.Database.GetHydrationAsync("FHIR-1"));
        Assert.Equal(
            ["Grace Example"],
            read.InPersonRequesters.Select(row => row.DisplayName).ToArray());
        Assert.Equal(
            1,
            Count(database, "prepared_ticket_in_person_requesters"));
    }

    [Fact]
    public async Task SaveHydration_PersistsOnlyDisplayNamesAndPolicyProofForRequesters()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SaveHydrationAsync(SampleBatch("FHIR-1"));

        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM pragma_table_info('prepared_ticket_in_person_requesters')
            ORDER BY cid
            """;
        List<string> columns = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.Equal(
            [
                "RowId",
                "TicketKey",
                "DisplayName",
                "PublicDisplayNamePolicyVersion",
            ],
            columns);
        Assert.DoesNotContain(
            columns,
            column =>
                column.Contains("user", StringComparison.OrdinalIgnoreCase) ||
                column.Contains("email", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SaveHydration_TrustedPeopleArePersistedAndUntrustedReplacementClearsThem()
    {
        using TestDatabase database = CreateDatabase();
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        PreparedTicketHydrationBatch trusted = SampleBatch("FHIR-1") with
        {
            Parent = SampleParent("FHIR-1", hydratedAt) with
            {
                Reporter = "  Ada Example ",
                Assignee = " Grace Example ",
                PublicDisplayNamePolicyVersion =
                    PublicDisplayNamePolicy.CurrentVersion,
            },
            JiraRows =
            [
                SampleJiraRow("FHIR-1", "FHIR-100", hydratedAt) with
                {
                    Reporter = "Related Reporter",
                    Assignee = "Related Assignee",
                    PublicDisplayNamePolicyVersion =
                        PublicDisplayNamePolicy.CurrentVersion,
                },
            ],
            InPersonRequesters =
            [
                new(
                    "FHIR-1",
                    "Requester Person",
                    PublicDisplayNamePolicy.CurrentVersion),
            ],
        };

        await database.Database.SaveHydrationAsync(trusted);

        PreparedTicketHydrationReadModel read =
            Assert.IsType<PreparedTicketHydrationReadModel>(
                await database.Database.GetHydrationAsync("FHIR-1"));
        Assert.Equal("Ada Example", read.Parent!.Reporter);
        Assert.Equal("Grace Example", read.Parent.Assignee);
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            read.Parent.PublicDisplayNamePolicyVersion);
        PreparedJiraHydrationRow jira = Assert.Single(read.JiraRows);
        Assert.Equal("Related Reporter", jira.Reporter);
        Assert.Equal("Related Assignee", jira.Assignee);
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            jira.PublicDisplayNamePolicyVersion);
        PreparedTicketInPersonRequesterRow requester =
            Assert.Single(read.InPersonRequesters);
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            requester.PublicDisplayNamePolicyVersion);

        int oldPolicyVersion = PublicDisplayNamePolicy.CurrentVersion - 1;
        PreparedTicketHydrationBatch untrusted = trusted with
        {
            Parent = trusted.Parent with
            {
                Reporter = "Replacement Reporter",
                Assignee = "Replacement Assignee",
                PublicDisplayNamePolicyVersion = oldPolicyVersion,
            },
            JiraRows =
            [
                trusted.JiraRows[0] with
                {
                    Reporter = "Replacement Related Reporter",
                    Assignee = "Replacement Related Assignee",
                    PublicDisplayNamePolicyVersion = oldPolicyVersion,
                },
            ],
            InPersonRequesters =
            [
                new(
                    "FHIR-1",
                    "Replacement Requester",
                    oldPolicyVersion),
            ],
        };

        await database.Database.SaveHydrationAsync(untrusted);

        read = Assert.IsType<PreparedTicketHydrationReadModel>(
            await database.Database.GetHydrationAsync("FHIR-1"));
        Assert.Null(read.Parent!.Reporter);
        Assert.Null(read.Parent.Assignee);
        Assert.Null(read.Parent.PublicDisplayNamePolicyVersion);
        jira = Assert.Single(read.JiraRows);
        Assert.Null(jira.Reporter);
        Assert.Null(jira.Assignee);
        Assert.Null(jira.PublicDisplayNamePolicyVersion);
        Assert.Empty(read.InPersonRequesters);
    }

    [Fact]
    public async Task SaveHydration_CurrentMarkerStillRejectsEmailValuedPeople()
    {
        using TestDatabase database = CreateDatabase();
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        PreparedTicketHydrationBatch batch = SampleBatch("FHIR-1") with
        {
            Parent = SampleParent("FHIR-1", hydratedAt) with
            {
                Reporter = "Ada <ada@example.org>",
                Assignee = "safe assignee",
                PublicDisplayNamePolicyVersion =
                    PublicDisplayNamePolicy.CurrentVersion,
            },
            JiraRows =
            [
                SampleJiraRow("FHIR-1", "FHIR-100", hydratedAt) with
                {
                    Reporter = "related@example.org",
                    Assignee = "Related <related@example.org>",
                    PublicDisplayNamePolicyVersion =
                        PublicDisplayNamePolicy.CurrentVersion,
                },
            ],
            InPersonRequesters =
            [
                new(
                    "FHIR-1",
                    "Safe Requester",
                    PublicDisplayNamePolicy.CurrentVersion),
                new(
                    "FHIR-1",
                    "Unsafe <unsafe@example.org>",
                    PublicDisplayNamePolicy.CurrentVersion),
            ],
        };

        await database.Database.SaveHydrationAsync(batch);

        PreparedTicketHydrationReadModel read =
            Assert.IsType<PreparedTicketHydrationReadModel>(
                await database.Database.GetHydrationAsync("FHIR-1"));
        Assert.Null(read.Parent!.Reporter);
        Assert.Equal("safe assignee", read.Parent.Assignee);
        PreparedJiraHydrationRow jira = Assert.Single(read.JiraRows);
        Assert.Null(jira.Reporter);
        Assert.Null(jira.Assignee);
        Assert.Equal(
            ["Safe Requester"],
            read.InPersonRequesters.Select(row => row.DisplayName).ToArray());
    }

    [Fact]
    public async Task NeutralSaveHydration_PreservesTrustedMarkersAndClearsOnUntrustedReplacement()
    {
        using TestDatabase database = CreateDatabase();
        IHydrationTargetDatabase target = database.Database;
        HydrationBatch trusted = SampleNeutralBatch(
            "FHIR-1",
            PublicDisplayNamePolicy.CurrentVersion);

        await target.SaveHydrationAsync(trusted, CancellationToken.None);

        PreparedTicketHydrationReadModel read =
            Assert.IsType<PreparedTicketHydrationReadModel>(
                await database.Database.GetHydrationAsync("FHIR-1"));
        Assert.Equal("Parent Reporter", read.Parent!.Reporter);
        Assert.Equal("Parent Assignee", read.Parent.Assignee);
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            read.Parent.PublicDisplayNamePolicyVersion);
        PreparedJiraHydrationRow jira = Assert.Single(read.JiraRows);
        Assert.Equal("Jira Reporter", jira.Reporter);
        Assert.Equal("Jira Assignee", jira.Assignee);
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            jira.PublicDisplayNamePolicyVersion);
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            Assert.Single(read.InPersonRequesters)
                .PublicDisplayNamePolicyVersion);

        HydrationBatch untrusted = SampleNeutralBatch(
            "FHIR-1",
            PublicDisplayNamePolicy.CurrentVersion + 1);
        await target.SaveHydrationAsync(untrusted, CancellationToken.None);

        read = Assert.IsType<PreparedTicketHydrationReadModel>(
            await database.Database.GetHydrationAsync("FHIR-1"));
        Assert.Null(read.Parent!.Reporter);
        Assert.Null(read.Parent.Assignee);
        Assert.Null(read.Parent.PublicDisplayNamePolicyVersion);
        jira = Assert.Single(read.JiraRows);
        Assert.Null(jira.Reporter);
        Assert.Null(jira.Assignee);
        Assert.Null(jira.PublicDisplayNamePolicyVersion);
        Assert.Empty(read.InPersonRequesters);
    }

    [Fact]
    public async Task SaveHydration_DoesNotTouchOtherTicketRows()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SaveHydrationAsync(SampleBatch("FHIR-1"));
        await database.Database.SaveHydrationAsync(SampleBatch("FHIR-2"));

        Assert.NotNull(await database.Database.GetHydrationAsync("FHIR-1"));
        Assert.NotNull(await database.Database.GetHydrationAsync("FHIR-2"));
        Assert.Equal(2, Count(database, "prepared_ticket_hydration"));
        Assert.Equal(2, Count(database, "prepared_jira_hydration"));
    }

    [Fact]
    public async Task SaveHydration_HonorsCompositeUniqueIndex()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SaveHydrationAsync(SampleBatch("FHIR-1"));
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        PreparedTicketHydrationBatch duplicate = new(
            TicketKey: "FHIR-9",
            Parent: SampleParent("FHIR-9", hydratedAt),
            JiraRows: [
                SampleJiraRow("FHIR-9", "FHIR-X", hydratedAt),
                SampleJiraRow("FHIR-9", "FHIR-X", hydratedAt),
            ],
            ZulipRows: [],
            GitHubRows: [],
            RepoRows: [],
            JiraXrefRows: []);

        await Assert.ThrowsAsync<SqliteException>(() => database.Database.SaveHydrationAsync(duplicate));

        Assert.Equal(1, Count(database, "prepared_ticket_hydration"));
        Assert.Equal(0, CountWhere(database, "prepared_ticket_hydration", "TicketKey = 'FHIR-9'"));
        Assert.Equal(0, CountWhere(database, "prepared_jira_hydration", "TicketKey = 'FHIR-9'"));
    }

    [Fact]
    public async Task SaveHydration_ParentUnresolvedDoesNotDropRelatedRows()
    {
        using TestDatabase database = CreateDatabase();
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        PreparedTicketHydrationBatch batch = new(
            TicketKey: "FHIR-1",
            Parent: new PreparedTicketHydrationRow(
                TicketKey: "FHIR-1",
                Priority: null,
                Resolution: null,
                ResolutionDescriptionPlain: null,
                Specification: null,
                RaisedInVersion: null,
                SelectedBallot: null,
                ChangeCategory: null,
                Impact: null,
                Labels: null,
                CommentCount: null,
                DescriptionPlain: null,
                HydratedAt: hydratedAt,
                HydrationStatus: "unresolved",
                HydrationReason: "orchestrator 503"),
            JiraRows: [SampleJiraRow("FHIR-1", "FHIR-100", hydratedAt)],
            ZulipRows: [SampleZulipRow("FHIR-1", "implementers:ballot", hydratedAt)],
            GitHubRows: [SampleGitHubRow("FHIR-1", "HL7/fhir#1", hydratedAt)],
            RepoRows: [SampleRepoRow("FHIR-1", "HL7/fhir", hydratedAt)],
            JiraXrefRows: []);

        await database.Database.SaveHydrationAsync(batch);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-1");
        Assert.NotNull(read);
        Assert.Equal("unresolved", read!.Parent!.HydrationStatus);
        Assert.Single(read.JiraRows);
        Assert.Single(read.ZulipRows);
        Assert.Single(read.GitHubRows);
        Assert.Single(read.RepoRows);
    }

    [Fact]
    public async Task ListJiraHydrationDisplayForWorkGroupAsync_ReturnsEmpty_WhenNoRows()
    {
        using TestDatabase database = CreateDatabase();

        IReadOnlyList<PreparedJiraHydrationRow> rows =
            await database.Database.ListJiraHydrationDisplayForWorkGroupAsync("OrdersAndObservations");

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ListJiraHydrationDisplayForWorkGroupAsync_ReturnsOnlySelfRows()
    {
        using TestDatabase database = CreateDatabase();
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-1",
            jiraKey: "FHIR-1",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-1",
            jiraKey: "FHIR-555",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");

        IReadOnlyList<PreparedJiraHydrationRow> rows =
            await database.Database.ListJiraHydrationDisplayForWorkGroupAsync("OrdersAndObservations");

        PreparedJiraHydrationRow only = Assert.Single(rows);
        Assert.Equal("FHIR-1", only.TicketKey);
        Assert.Equal("FHIR-1", only.JiraKey);
    }

    [Fact]
    public async Task ListJiraHydrationDisplayForWorkGroupAsync_MatchesWorkGroupClean()
    {
        using TestDatabase database = CreateDatabase();
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-1",
            jiraKey: "FHIR-1",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-2",
            jiraKey: "FHIR-2",
            workGroup: "Patient Care",
            type: "Change Request",
            specification: "FHIR Core");

        IReadOnlyList<PreparedJiraHydrationRow> rows =
            await database.Database.ListJiraHydrationDisplayForWorkGroupAsync("OrdersAndObservations");

        PreparedJiraHydrationRow only = Assert.Single(rows);
        Assert.Equal("FHIR-1", only.TicketKey);
        Assert.Equal("Orders and Observations", only.WorkGroup);
    }

    [Fact]
    public async Task ListJiraHydrationDisplayForWorkGroupAsync_OrdersByTicketKey()
    {
        using TestDatabase database = CreateDatabase();
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-3",
            jiraKey: "FHIR-3",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-1",
            jiraKey: "FHIR-1",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-2",
            jiraKey: "FHIR-2",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");

        IReadOnlyList<PreparedJiraHydrationRow> rows =
            await database.Database.ListJiraHydrationDisplayForWorkGroupAsync("OrdersAndObservations");

        Assert.Equal(["FHIR-1", "FHIR-2", "FHIR-3"], rows.Select(r => r.TicketKey).ToArray());
    }

    [Fact]
    public async Task ListJiraHydrationDisplayForWorkGroupAsync_IncludesNonOkStatus()
    {
        using TestDatabase database = CreateDatabase();
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-404",
            jiraKey: "FHIR-404",
            workGroup: "Orders and Observations",
            type: null,
            specification: null,
            title: null,
            status: null,
            hydrationStatus: "NotFound",
            hydrationReason: "jira returned 404");

        IReadOnlyList<PreparedJiraHydrationRow> rows =
            await database.Database.ListJiraHydrationDisplayForWorkGroupAsync("OrdersAndObservations");

        PreparedJiraHydrationRow only = Assert.Single(rows);
        Assert.Equal("FHIR-404", only.TicketKey);
        Assert.Equal("NotFound", only.HydrationStatus);
        Assert.Equal("jira returned 404", only.HydrationReason);
        Assert.Null(only.Title);
        Assert.Null(only.Status);
        Assert.Null(only.Type);
        Assert.Null(only.Specification);
    }

    private static async Task SeedHydrationRowAsync(
        PreparerDatabase database,
        string ticketKey,
        string jiraKey,
        string workGroup,
        string? type,
        string? specification,
        string? title = "title",
        string? status = "Open",
        string hydrationStatus = "resolved",
        string? hydrationReason = null)
    {
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        await using SqliteConnection connection = database.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_jira_hydration
            (Id, TicketKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain, WorkGroup, WorkGroupClean, Specification, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason)
            VALUES
            (@id, @ticket, @jira, @title, @status, @type, NULL, NULL, NULL, @workGroup, @workGroupClean, @specification, @updatedAt, @url, @hydratedAt, @hydrationStatus, @hydrationReason)
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@ticket", ticketKey);
        command.Parameters.AddWithValue("@jira", jiraKey);
        command.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("@type", (object?)type ?? DBNull.Value);
        command.Parameters.AddWithValue("@workGroup", workGroup);
        string workGroupCleanRaw = FhirAugury.Common.WorkGroups.Hl7WorkGroupNameCleaner.Clean(workGroup);
        command.Parameters.AddWithValue("@workGroupClean", string.IsNullOrEmpty(workGroupCleanRaw) ? (object)DBNull.Value : workGroupCleanRaw);
        command.Parameters.AddWithValue("@specification", (object?)specification ?? DBNull.Value);
        command.Parameters.AddWithValue("@updatedAt", hydratedAt.ToString("O"));
        command.Parameters.AddWithValue("@url", $"https://jira.example.com/{jiraKey}");
        command.Parameters.AddWithValue("@hydratedAt", hydratedAt.ToString("O"));
        command.Parameters.AddWithValue("@hydrationStatus", hydrationStatus);
        command.Parameters.AddWithValue("@hydrationReason", (object?)hydrationReason ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task GetClusteringSignalsAsync_ReturnsNull_WhenWorkgroupHasNoHydration()
    {
        using TestDatabase database = CreateDatabase();

        PreparedTicketClusteringSignals? signals =
            await database.Database.GetClusteringSignalsAsync("OrdersAndObservations");

        Assert.Null(signals);
    }

    [Fact]
    public async Task GetClusteringSignalsAsync_JoinsPreparedSummariesAndLinks()
    {
        using TestDatabase database = CreateDatabase();
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-1",
            jiraKey: "FHIR-1",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core",
            title: "Observation polymorphic value",
            status: "Open");
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-2",
            jiraKey: "FHIR-2",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core",
            title: "Companion ticket",
            status: "Resolved");

        await database.Database.SavePreparedTicketAsync(new PreparedTicketPayload
        {
            Key = "FHIR-1",
            RequestSummary = "request-1",
            CommentSummary = "comments-1",
            LinkedTicketSummary = "linked-1",
            RelatedTicketSummary = "related-1",
            RelatedZulipSummary = "zulip-1",
            RelatedGitHubSummary = "github-1",
            ExistingProposed = "existing-1",
            ProposalA = "A",
            ProposalAJustification = "a",
            ProposalAImpact = "Non-substantive",
            ProposalB = "B",
            ProposalBJustification = "b",
            ProposalBImpact = "Non-substantive",
            ProposalC = "C",
            ProposalCJustification = "c",
            Recommendation = "A",
            RecommendationJustification = "because",
            SavedAt = DateTimeOffset.Parse("2026-05-18T00:00:00Z"),
            RelatedJiraTickets =
            [
                new PreparedTicketRelatedJiraPayload { AssociatedTicketKey = "FHIR-2", LinkType = "linked", Justification = "shared field" },
                new PreparedTicketRelatedJiraPayload { AssociatedTicketKey = "FHIR-9", LinkType = "related", Justification = "near-by" },
            ],
        });
        await database.Database.SavePreparedTicketAsync(new PreparedTicketPayload
        {
            Key = "FHIR-2",
            RequestSummary = "request-2",
            CommentSummary = "comments-2",
            LinkedTicketSummary = "linked-2",
            RelatedTicketSummary = "related-2",
            RelatedZulipSummary = "zulip-2",
            RelatedGitHubSummary = "github-2",
            ExistingProposed = "existing-2",
            ProposalA = "A",
            ProposalAJustification = "a",
            ProposalAImpact = "Non-substantive",
            ProposalB = "B",
            ProposalBJustification = "b",
            ProposalBImpact = "Non-substantive",
            ProposalC = "C",
            ProposalCJustification = "c",
            Recommendation = "A",
            RecommendationJustification = "because",
            SavedAt = DateTimeOffset.Parse("2026-05-18T00:00:00Z"),
        });

        PreparedTicketClusteringSignals? signals =
            await database.Database.GetClusteringSignalsAsync("OrdersAndObservations");

        Assert.NotNull(signals);
        Assert.Equal("OrdersAndObservations", signals!.WorkGroupClean);
        Assert.Equal("Orders and Observations", signals.WorkGroupDisplay);
        Assert.Equal(2, signals.Tickets.Count);

        PreparedTicketClusteringSignal first = signals.Tickets[0];
        Assert.Equal("FHIR-1", first.TicketKey);
        Assert.Equal("Observation polymorphic value", first.Title);
        Assert.Equal("Open", first.Status);
        Assert.Equal("FHIR Core", first.Specification);
        Assert.Equal("Change Request", first.Type);
        Assert.Equal("request-1", first.RequestSummary);
        Assert.Equal("comments-1", first.CommentSummary);
        Assert.True(first.HasPreparedTicket);
        Assert.Equal(2, first.Links.Count);
        Assert.Contains(first.Links, l => l.AssociatedTicketKey == "FHIR-2" && l.LinkType == "linked");
        Assert.Contains(first.Links, l => l.AssociatedTicketKey == "FHIR-9" && l.LinkType == "related");

        PreparedTicketClusteringSignal second = signals.Tickets[1];
        Assert.Equal("FHIR-2", second.TicketKey);
        Assert.True(second.HasPreparedTicket);
        Assert.Empty(second.Links);
    }

    [Fact]
    public async Task GetClusteringSignalsAsync_UsesReplaceWorkGroupConvention()
    {
        using TestDatabase database = CreateDatabase();
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-1",
            jiraKey: "FHIR-1",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-2",
            jiraKey: "FHIR-2",
            workGroup: "Patient Care",
            type: "Change Request",
            specification: "FHIR Core");

        PreparedTicketClusteringSignals? signals =
            await database.Database.GetClusteringSignalsAsync("OrdersAndObservations");

        Assert.NotNull(signals);
        PreparedTicketClusteringSignal only = Assert.Single(signals!.Tickets);
        Assert.Equal("FHIR-1", only.TicketKey);
    }

    [Fact]
    public async Task GetClusteringSignalsAsync_EmitsHydrationOnlyTicketWithEmptySummaries()
    {
        using TestDatabase database = CreateDatabase();
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-1",
            jiraKey: "FHIR-1",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");

        PreparedTicketClusteringSignals? signals =
            await database.Database.GetClusteringSignalsAsync("OrdersAndObservations");

        Assert.NotNull(signals);
        PreparedTicketClusteringSignal only = Assert.Single(signals!.Tickets);
        Assert.Equal("FHIR-1", only.TicketKey);
        Assert.False(only.HasPreparedTicket);
        Assert.Equal(string.Empty, only.RequestSummary);
        Assert.Equal(string.Empty, only.CommentSummary);
        Assert.Equal(string.Empty, only.LinkedTicketSummary);
        Assert.Equal(string.Empty, only.RelatedTicketSummary);
        Assert.Equal(string.Empty, only.RelatedZulipSummary);
        Assert.Equal(string.Empty, only.RelatedGitHubSummary);
        Assert.Empty(only.Links);
    }

    [Fact]
    public async Task GetClusteringSignalsAsync_IgnoresNonSelfHydrationRowsForLinks()
    {
        using TestDatabase database = CreateDatabase();
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-1",
            jiraKey: "FHIR-1",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");
        // Non-self row: same TicketKey but different JiraKey — must not
        // double-count or surface a second clustering row.
        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-1",
            jiraKey: "FHIR-555",
            workGroup: "Orders and Observations",
            type: "Change Request",
            specification: "FHIR Core");

        PreparedTicketClusteringSignals? signals =
            await database.Database.GetClusteringSignalsAsync("OrdersAndObservations");

        Assert.NotNull(signals);
        PreparedTicketClusteringSignal only = Assert.Single(signals!.Tickets);
        Assert.Equal("FHIR-1", only.TicketKey);
    }

    [Fact]
    public async Task GetClusteringSignalsAsync_OrdersByTicketKey()
    {
        using TestDatabase database = CreateDatabase();
        await SeedHydrationRowAsync(database.Database, "FHIR-3", "FHIR-3", "Orders and Observations", "Change Request", "FHIR Core");
        await SeedHydrationRowAsync(database.Database, "FHIR-1", "FHIR-1", "Orders and Observations", "Change Request", "FHIR Core");
        await SeedHydrationRowAsync(database.Database, "FHIR-2", "FHIR-2", "Orders and Observations", "Change Request", "FHIR Core");

        PreparedTicketClusteringSignals? signals =
            await database.Database.GetClusteringSignalsAsync("OrdersAndObservations");

        Assert.NotNull(signals);
        Assert.Equal(["FHIR-1", "FHIR-2", "FHIR-3"], signals!.Tickets.Select(s => s.TicketKey).ToArray());
    }

    [Fact]
    public void SnapshotSchemaV3_ExtendsV2WithoutMutatingOlderCatalogs()
    {
        Assert.DoesNotContain(
            AuthoringSnapshotSchemaV1.CoreTables,
            table => table.Name == "authoring_run_input_provenance");
        Assert.DoesNotContain(
            PreparedTicketSnapshotSchemaV1.Tables,
            table => table.Name == "prepared_ticket_in_person_requesters");
        Assert.DoesNotContain(
            PreparedTicketSnapshotSchemaV1.Tables.Single(
                table => table.Name == "prepared_ticket_hydration").Columns,
            column => column == "Assignee");

        Assert.Contains(
            AuthoringSnapshotSchemaV2.CoreTables,
            table => table.Name == "authoring_run_input_provenance");
        Assert.Contains(
            PreparedTicketSnapshotSchemaV2.Tables,
            table => table.Name == "prepared_ticket_in_person_requesters");
        Assert.Contains(
            PreparedTicketSnapshotSchemaV2.Tables.Single(
                table => table.Name == "prepared_ticket_hydration").Columns,
            column => column == "SourceContentRevision");
        Assert.DoesNotContain(
            PreparedTicketSnapshotSchemaV2.Tables.SelectMany(
                table => table.Columns),
            column => column == "PublicDisplayNamePolicyVersion");
        Assert.Equal(
            PreparedTicketSnapshotSchemaV2.Tables.Select(table => table.Name),
            PreparedTicketSnapshotSchemaV3.Tables.Select(table => table.Name));
        foreach (AuthoringSnapshotTableSchema v2Table in
                 PreparedTicketSnapshotSchemaV2.Tables)
        {
            AuthoringSnapshotTableSchema v3Table =
                PreparedTicketSnapshotSchemaV3.Tables.Single(
                    table => table.Name == v2Table.Name);
            string[] expectedColumns = v2Table.Name is
                "prepared_ticket_hydration" or
                "prepared_jira_hydration" or
                "prepared_ticket_in_person_requesters"
                    ? [.. v2Table.Columns, "PublicDisplayNamePolicyVersion"]
                    : [.. v2Table.Columns];
            Assert.Equal(expectedColumns, v3Table.Columns);
        }
        Assert.Same(
            PreparedTicketSnapshotSchemaV1.Catalog,
            PreparedTicketSnapshotSchemaResolver.Resolve(1));
        Assert.Same(
            PreparedTicketSnapshotSchemaV2.Catalog,
            PreparedTicketSnapshotSchemaResolver.Resolve(2));
        Assert.Same(
            PreparedTicketSnapshotSchemaV3.Catalog,
            PreparedTicketSnapshotSchemaResolver.Resolve(3));
        Assert.True(
            PreparedTicketSnapshotSchemaResolver.TryResolve(
                3,
                out AuthoringSnapshotSchemaCatalog? resolvedV3));
        Assert.Same(PreparedTicketSnapshotSchemaV3.Catalog, resolvedV3);
        Assert.False(
            PreparedTicketSnapshotSchemaResolver.TryResolve(
                4,
                out AuthoringSnapshotSchemaCatalog? unsupported));
        Assert.Null(unsupported);
        Assert.Equal(
            [1, 2, 3],
            PreparedTicketSnapshotSchemaResolver.SupportedVersions);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PreparedTicketSnapshotSchemaResolver.Resolve(4));
    }

    [Fact]
    public async Task SnapshotCounts_AreSelectedBySchemaVersion()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SaveHydrationAsync(SampleBatch("FHIR-1"));

        IReadOnlyDictionary<string, long> v1 =
            await database.Database.GetSnapshotTableCountsAsync(1);
        IReadOnlyDictionary<string, long> v2 =
            await database.Database.GetSnapshotTableCountsAsync(2);
        IReadOnlyDictionary<string, long> v3 =
            await database.Database.GetSnapshotTableCountsAsync(3);

        Assert.Equal(
            PreparedTicketSnapshotSchemaV1.CountedTables.Order(),
            v1.Keys.Order());
        Assert.Equal(
            PreparedTicketSnapshotSchemaV2.CountedTables.Order(),
            v2.Keys.Order());
        Assert.Equal(
            PreparedTicketSnapshotSchemaV3.CountedTables.Order(),
            v3.Keys.Order());
        Assert.False(v1.ContainsKey(
            "prepared_ticket_in_person_requesters"));
        Assert.Equal(
            2,
            v2["prepared_ticket_in_person_requesters"]);
        Assert.Equal(
            2,
            v3["prepared_ticket_in_person_requesters"]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => database.Database.GetSnapshotTableCountsAsync(99));
    }

    [Fact]
    public async Task Initialize_MigratesLegacyHydrationSchemaAdditively()
    {
        string directory = Path.Combine(
            Environment.CurrentDirectory,
            "temp",
            "preparer-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string dbPath = Path.Combine(directory, "preparer.db");

        PreparerDatabase legacy = new(
            dbPath,
            NullLogger<PreparerDatabase>.Instance);
        legacy.Initialize();
        await legacy.SaveHydrationAsync(SampleBatch("FHIR-1"));
        using (SqliteConnection connection = legacy.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE prepared_ticket_hydration
                SET Reporter = 'legacy-parent-user'
                WHERE TicketKey = 'FHIR-1';
                UPDATE prepared_jira_hydration
                SET Reporter = 'legacy-related-user'
                WHERE TicketKey = 'FHIR-1';
                DELETE FROM schema_migrations
                WHERE Name = 'prepared-hydration-structured-people-v2';
                DROP INDEX IF EXISTS idx_prepared_ticket_in_person_requesters_ticket_name;
                DROP INDEX IF EXISTS IDX_prepared_ticket_in_person_requesters_TicketKey;
                DROP TABLE prepared_ticket_in_person_requesters;
                ALTER TABLE prepared_ticket_hydration DROP COLUMN Assignee;
                ALTER TABLE prepared_ticket_hydration DROP COLUMN SourceProject;
                ALTER TABLE prepared_ticket_hydration DROP COLUMN SourceLastSuccessfulRefreshAt;
                ALTER TABLE prepared_ticket_hydration DROP COLUMN SourceContentRevision;
                ALTER TABLE prepared_jira_hydration DROP COLUMN Assignee;
                """;
            command.ExecuteNonQuery();
        }
        legacy.Dispose();

        PreparerDatabase upgraded = new(
            dbPath,
            NullLogger<PreparerDatabase>.Instance);
        upgraded.Initialize();
        try
        {
            using SqliteConnection connection = upgraded.OpenConnection();
            Assert.Equal(
                4,
                ScalarInt(
                    connection,
                    """
                    SELECT COUNT(*)
                    FROM pragma_table_info('prepared_ticket_hydration')
                    WHERE name IN (
                        'Assignee',
                        'SourceProject',
                        'SourceLastSuccessfulRefreshAt',
                        'SourceContentRevision')
                    """));
            Assert.Equal(
                1,
                ScalarInt(
                    connection,
                    """
                    SELECT COUNT(*)
                    FROM pragma_table_info('prepared_jira_hydration')
                    WHERE name = 'Assignee'
                    """));
            Assert.Equal(
                1,
                ScalarInt(
                    connection,
                    """
                    SELECT COUNT(*)
                    FROM prepared_ticket_hydration
                    WHERE TicketKey = 'FHIR-1'
                    """));
            Assert.Equal(
                0,
                ScalarInt(
                    connection,
                    """
                    SELECT COUNT(*)
                    FROM prepared_ticket_hydration
                    WHERE Reporter IS NOT NULL
                    """));
            using (SqliteCommand status = connection.CreateCommand())
            {
                status.CommandText =
                    """
                    SELECT HydrationStatus
                    FROM prepared_ticket_hydration
                    WHERE TicketKey = 'FHIR-1'
                    """;
                Assert.Equal("unresolved", status.ExecuteScalar());
            }
            Assert.Equal(
                0,
                ScalarInt(
                    connection,
                    """
                    SELECT COUNT(*)
                    FROM prepared_jira_hydration
                    WHERE Reporter IS NOT NULL
                    """));
            Assert.Equal(
                0,
                ScalarInt(
                    connection,
                    "SELECT COUNT(*) FROM prepared_ticket_in_person_requesters"));
        }
        finally
        {
            upgraded.Dispose();
            SqliteConnection.ClearAllPools();
            TestFileCleanup.SafeDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Initialize_AddsNullablePeoplePolicyMarkersWithoutTrustingExistingRows()
    {
        string directory = Path.Combine(
            Environment.CurrentDirectory,
            "temp",
            "preparer-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string dbPath = Path.Combine(directory, "preparer.db");

        PreparerDatabase legacy = new(
            dbPath,
            NullLogger<PreparerDatabase>.Instance);
        legacy.Initialize();
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        PreparedTicketHydrationBatch batch = SampleBatch("FHIR-1") with
        {
            Parent = SampleParent("FHIR-1", hydratedAt) with
            {
                Reporter = "Legacy Parent",
                Assignee = "Legacy Parent Assignee",
                PublicDisplayNamePolicyVersion =
                    PublicDisplayNamePolicy.CurrentVersion,
            },
            JiraRows =
            [
                SampleJiraRow("FHIR-1", "FHIR-100", hydratedAt) with
                {
                    Reporter = "Legacy Jira",
                    Assignee = "Legacy Jira Assignee",
                    PublicDisplayNamePolicyVersion =
                        PublicDisplayNamePolicy.CurrentVersion,
                },
            ],
        };
        await legacy.SaveHydrationAsync(batch);
        using (SqliteConnection connection = legacy.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                ALTER TABLE prepared_ticket_hydration
                DROP COLUMN PublicDisplayNamePolicyVersion;
                ALTER TABLE prepared_jira_hydration
                DROP COLUMN PublicDisplayNamePolicyVersion;
                ALTER TABLE prepared_ticket_in_person_requesters
                DROP COLUMN PublicDisplayNamePolicyVersion;
                """;
            command.ExecuteNonQuery();
        }
        legacy.Dispose();

        PreparerDatabase upgraded = new(
            dbPath,
            NullLogger<PreparerDatabase>.Instance);
        upgraded.Initialize();
        try
        {
            using SqliteConnection connection = upgraded.OpenConnection();
            foreach (string table in new[]
                     {
                         "prepared_ticket_hydration",
                         "prepared_jira_hydration",
                         "prepared_ticket_in_person_requesters",
                     })
            {
                using SqliteCommand column = connection.CreateCommand();
                column.CommandText =
                    $"""
                    SELECT COUNT(*)
                    FROM pragma_table_info('{table}')
                    WHERE name = 'PublicDisplayNamePolicyVersion'
                    """;
                Assert.Equal(1, Convert.ToInt32(column.ExecuteScalar()));

                using SqliteCommand marker = connection.CreateCommand();
                marker.CommandText =
                    $"""
                    SELECT COUNT(*)
                    FROM {table}
                    WHERE PublicDisplayNamePolicyVersion IS NOT NULL
                    """;
                Assert.Equal(0, Convert.ToInt32(marker.ExecuteScalar()));
            }
            Assert.Equal(
                1,
                ScalarInt(
                    connection,
                    """
                    SELECT COUNT(*)
                    FROM prepared_ticket_hydration
                    WHERE Reporter = 'Legacy Parent'
                      AND Assignee = 'Legacy Parent Assignee'
                    """));
            Assert.Equal(
                2,
                ScalarInt(
                    connection,
                    "SELECT COUNT(*) FROM prepared_ticket_in_person_requesters"));
            PreparedTicketHydrationReadModel read =
                Assert.IsType<PreparedTicketHydrationReadModel>(
                    await upgraded.GetHydrationAsync("FHIR-1"));
            Assert.Null(read.Parent!.Reporter);
            Assert.Null(read.Parent.Assignee);
            Assert.Null(read.Parent.PublicDisplayNamePolicyVersion);
            Assert.All(
                read.JiraRows,
                row =>
                {
                    Assert.Null(row.Reporter);
                    Assert.Null(row.Assignee);
                    Assert.Null(row.PublicDisplayNamePolicyVersion);
                });
            Assert.Empty(read.InPersonRequesters);
        }
        finally
        {
            upgraded.Dispose();
            SqliteConnection.ClearAllPools();
            TestFileCleanup.SafeDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Initialize_SeedsNullJiraProvenanceForLegacyRuns()
    {
        string directory = Path.Combine(
            Environment.CurrentDirectory,
            "temp",
            "preparer-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string dbPath = Path.Combine(directory, "preparer.db");
        AuthoringRunRecord run;

        PreparerDatabase legacy = new(
            dbPath,
            NullLogger<PreparerDatabase>.Instance);
        legacy.Initialize();
        AuthoringRunStore store = new(legacy);
        await store.EnsureProcessorModeAsync("jira-fhir");
        await store.TransitionProcessorModeAsync(
            "jira-fhir",
            AuthoringStatusValues.ProcessorModes.Legacy,
            AuthoringStatusValues.ProcessorModes.CuttingOver);
        await store.TransitionProcessorModeAsync(
            "jira-fhir",
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            AuthoringStatusValues.ProcessorModes.RunBacked);
        run = await store.CreateRunAsync(
            "jira-fhir",
            [new AuthoringRunItemDefinition("FHIR-1", "fhir", "revision")]);
        Assert.Empty(await store.GetRunInputProvenanceAsync(run.Id));
        legacy.Dispose();

        PreparerDatabase upgraded = new(
            dbPath,
            NullLogger<PreparerDatabase>.Instance);
        upgraded.Initialize();
        try
        {
            AuthoringRunInputProvenanceRecord provenance = Assert.Single(
                await new AuthoringRunStore(upgraded)
                    .GetRunInputProvenanceAsync(run.Id));
            Assert.Equal("jira", provenance.Source);
            Assert.Null(provenance.LatestSuccessfulRefreshAt);
            Assert.Null(provenance.ContentRevision);
            Assert.Equal(run.CreatedAt, provenance.CapturedAt);
        }
        finally
        {
            upgraded.Dispose();
            SqliteConnection.ClearAllPools();
            TestFileCleanup.SafeDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task PrepareCutover_DerivesConservativeJiraProvenance()
    {
        using TestDatabase database = CreateDatabase();
        await database.Database.SavePreparedTicketAsync(
            SamplePayload("FHIR-1"));
        await database.Database.SavePreparedTicketAsync(
            SamplePayload("FHIR-2"));
        JiraProcessingSourceTicketStore sourceStore = new(
            database.Database.DatabasePath);
        DateTimeOffset firstRefresh =
            new(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset secondRefresh = firstRefresh.AddDays(1);
        JiraIssueSummaryEntry first = SourceTicket("FHIR-1");
        JiraIssueSummaryEntry second = SourceTicket("FHIR-2");
        await sourceStore.UpsertAsync(
            first,
            "fhir",
            false,
            firstRefresh,
            77,
            CancellationToken.None);
        await sourceStore.UpsertAsync(
            second,
            "fhir",
            false,
            secondRefresh,
            77,
            CancellationToken.None);

        await using SqliteConnection connection =
            database.Database.OpenConnection();
        AuthoringCutoverPreparation preparation =
            await database.Database.PrepareCutoverAsync(
                connection,
                CancellationToken.None);

        Assert.Equal(2, preparation.Items.Count);
        AuthoringRunInputProvenanceDefinition provenance =
            Assert.Single(preparation.InputProvenance!);
        Assert.Equal("jira", provenance.Source);
        Assert.Equal(secondRefresh, provenance.LatestSuccessfulRefreshAt);
        Assert.Equal(77, provenance.ContentRevision);

        await sourceStore.UpsertAsync(
            second,
            "fhir",
            false,
            sourceProjectLastSuccessfulRefreshAt: null,
            sourceContentRevision: null,
            ct: CancellationToken.None);
        preparation = await database.Database.PrepareCutoverAsync(
            connection,
            CancellationToken.None);
        provenance = Assert.Single(preparation.InputProvenance!);
        Assert.Null(provenance.LatestSuccessfulRefreshAt);
        Assert.Null(provenance.ContentRevision);
    }

    private static Task<PublicationRefreshContext>
        CreatePublicationRefreshContextAsync(
            TestDatabase database,
            params string[] ticketKeys)
        => CreatePublicationRefreshContextAsync(
            database,
            key => $"revision-{key}",
            ticketKeys);

    private static async Task<PublicationRefreshContext>
        CreatePublicationRefreshContextAsync(
            TestDatabase database,
            Func<string, string> sourceRevisionFactory,
            params string[] ticketKeys)
    {
        AuthoringRunStore store = new(database.Database);
        await store.EnsureProcessorModeAsync("jira-fhir");
        await store.TransitionProcessorModeAsync(
            "jira-fhir",
            AuthoringStatusValues.ProcessorModes.Legacy,
            AuthoringStatusValues.ProcessorModes.CuttingOver);
        await store.TransitionProcessorModeAsync(
            "jira-fhir",
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            AuthoringStatusValues.ProcessorModes.RunBacked);
        AuthoringRunRecord sourceRun = await store.CreateRunAsync(
            "jira-fhir",
            ticketKeys.Select(key =>
                new AuthoringRunItemDefinition(
                    key,
                    "fhir",
                    sourceRevisionFactory(key))).ToArray());
        Assert.True(await store.TryAcquireMutationFenceAsync(
            "jira-fhir",
            sourceRun.Id));

        IReadOnlyList<AuthoringRunItemRecord> items =
            await store.GetRunItemsAsync(sourceRun.Id);
        foreach (AuthoringRunItemRecord item in items)
        {
            AuthoringOperationClaim claim =
                Assert.IsType<AuthoringOperationClaim>(
                    await store.ClaimItemAsync(
                        sourceRun.Id,
                        item.Id));
            PreparedTicketPayload payload = SamplePayload(item.BusinessKey);
            string contentHash =
                FhirAugury.Processor.Jira.Fhir.Preparer.Api
                    .PreparedTicketAuthoringDtos.ComputeContentHash(payload);
            AuthoringReceiptAcceptance accepted =
                await store.AcceptResultAsync(
                    new AuthoringResultSubmission(
                        sourceRun.Id,
                        item.Id,
                        claim.OperationId,
                        item.ExpectedSourceRevision,
                        contentHash),
                    claim.OperationToken,
                    (connection, ct) =>
                        database.Database
                            .SavePreparedTicketForAuthoringAsync(
                                connection,
                                payload,
                                contentHash,
                                sourceRun.Id,
                                item.Id,
                                claim.OperationId,
                                ct));
            await store.MarkItemCompleteAsync(
                item.Id,
                accepted.Receipt.ReceiptId);

            DateTimeOffset oldHydratedAt =
                new(2002, 2, 2, 0, 0, 0, TimeSpan.Zero);
            PreparedTicketHydrationBatch batch =
                SampleBatch(item.BusinessKey, item.BusinessKey);
            await database.Database.SaveHydrationAsync(
                batch with
                {
                    Parent = batch.Parent with
                    {
                        Reporter = "Legacy Reporter",
                        Assignee = "Legacy Assignee",
                        SourceProject = "LEGACY",
                        SourceLastSuccessfulRefreshAt =
                            oldHydratedAt.AddDays(-1),
                        SourceContentRevision = 2,
                        HydratedAt = oldHydratedAt,
                        HydrationStatus = "unresolved",
                        HydrationReason = "legacy publication metadata",
                        PublicDisplayNamePolicyVersion =
                            PublicDisplayNamePolicy.CurrentVersion,
                    },
                    JiraRows =
                    [
                        batch.JiraRows[0] with
                        {
                            Reporter = "Legacy Reporter",
                            Assignee = "Legacy Assignee",
                            PublicDisplayNamePolicyVersion =
                                PublicDisplayNamePolicy.CurrentVersion,
                        },
                        SampleJiraRow(
                            item.BusinessKey,
                            "FHIR-999",
                            oldHydratedAt),
                    ],
                    InPersonRequesters =
                    [
                        new PreparedTicketInPersonRequesterRow(
                            item.BusinessKey,
                            "Legacy Requester",
                            PublicDisplayNamePolicy.CurrentVersion),
                    ],
                });
        }

        await using (SqliteConnection connection =
                     database.Database.OpenConnection())
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO authoring_run_input_provenance(
                    RunId, Source, LatestSuccessfulRefreshAt,
                    ContentRevision, CapturedAt)
                VALUES(
                    @runId, 'jira',
                    '2001-01-01T00:00:00.0000000+00:00', 1,
                    '2001-01-01T00:00:00.0000000+00:00')
                """;
            command.Parameters.AddWithValue("@runId", sourceRun.Id);
            await command.ExecuteNonQueryAsync();
        }
        await store.ReleaseMutationFenceAsync("jira-fhir", sourceRun.Id);

        PreparedTicketPublicationRefreshInventory inventory =
            await database.Database.GetPublicationRefreshInventoryAsync();
        Assert.Equal(ticketKeys.Length, inventory.Candidates.Count);
        Assert.All(
            inventory.Candidates,
            candidate => Assert.Equal("fhir", candidate.ItemKind));

        string refreshRunId = Guid.NewGuid().ToString("N");
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        await using (SqliteConnection connection =
                     database.Database.OpenConnection())
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO authoring_runs(
                    Id, ProcessorKind, AuthoringEpoch, Status, Purpose,
                    SourceRunId, DatabaseOnly, TotalItems,
                    CreatedAt, StartedAt)
                SELECT @refreshRunId, ProcessorKind, AuthoringEpoch,
                       'running', 'publication-refresh', Id, 0,
                       @totalItems, @createdAt, @createdAt
                FROM authoring_runs
                WHERE Id = @sourceRunId;

                INSERT INTO authoring_run_items(
                    Id, RunId, BusinessKey, ItemKind,
                    ExpectedSourceRevision, Status, AcceptedReceiptId,
                    AttemptCount, CreatedAt, CompletedAt)
                SELECT @itemPrefix || Id, @refreshRunId, BusinessKey,
                       'maintenance:' || @refreshRunId || ':' || ItemKind,
                       ExpectedSourceRevision, 'complete',
                       AcceptedReceiptId, 0, @createdAt, @createdAt
                FROM authoring_run_items
                WHERE RunId = @sourceRunId;

                INSERT INTO authoring_mutation_fences(
                    ProcessorKind, RunId, LeaseId, AcquiredAt)
                VALUES(
                    'jira-fhir', @refreshRunId, @fenceLeaseId, @createdAt);
                """;
            command.Parameters.AddWithValue(
                "@refreshRunId",
                refreshRunId);
            command.Parameters.AddWithValue(
                "@sourceRunId",
                sourceRun.Id);
            command.Parameters.AddWithValue(
                "@totalItems",
                ticketKeys.Length);
            command.Parameters.AddWithValue(
                "@createdAt",
                createdAt.ToString("O"));
            command.Parameters.AddWithValue(
                "@itemPrefix",
                $"refresh-{refreshRunId}-");
            command.Parameters.AddWithValue(
                "@fenceLeaseId",
                Guid.NewGuid().ToString("N"));
            await command.ExecuteNonQueryAsync();
        }

        string inputFingerprint =
            PreparedTicketPublicationContract
                .ComputePublicationRefreshInputFingerprint(
                    sourceRun.Id,
                    inventory.Candidates.Select(candidate =>
                        candidate.ToPublicationCorpusItem()));
        AuthoringRunStageRecord stage = await store.EnsureRunStageAsync(
            refreshRunId,
            PreparerDatabase.PublicationMetadataStageName,
            string.Empty,
            inputFingerprint);
        AuthoringRunStageLease lease =
            Assert.IsType<AuthoringRunStageLease>(
                await store.TryStartRunStageAsync(stage.Id));
        return new PublicationRefreshContext(
            sourceRun.Id,
            refreshRunId,
            inventory,
            inputFingerprint,
            lease);
    }

    private static PreparedTicketPublicationMetadata PublicationMetadata(
        string ticketKey,
        string sourceRevision,
        long contentRevision,
        DateTimeOffset refreshAt)
        => new(
            ticketKey,
            sourceRevision,
            $"Reporter {ticketKey}",
            $"Assignee {ticketKey}",
            [$"Requester {ticketKey}"],
            ticketKey[..ticketKey.IndexOf('-', StringComparison.Ordinal)],
            refreshAt,
            contentRevision,
            true,
            PublicDisplayNamePolicy.CurrentVersion,
            refreshAt.AddMinutes(1));

    private static string ReadProtectedPublicationState(
        TestDatabase database)
    {
        string[] queries =
        [
            "SELECT * FROM prepared_tickets ORDER BY RowId",
            "SELECT * FROM prepared_ticket_repos ORDER BY RowId",
            "SELECT * FROM prepared_ticket_related_jira ORDER BY RowId",
            "SELECT * FROM prepared_ticket_related_zulip ORDER BY RowId",
            "SELECT * FROM prepared_ticket_related_github ORDER BY RowId",
            """
            SELECT RowId, Id, TicketKey, Priority, Resolution,
                   ResolutionDescriptionPlain, Specification,
                   RaisedInVersion, SelectedBallot, ChangeCategory,
                   Impact, Labels, CommentCount, DescriptionPlain,
                   DescriptionHtml, ResolutionDescriptionHtml, CreatedAt,
                   RelatedArtifactsRaw, RelatedPagesRaw
            FROM prepared_ticket_hydration ORDER BY RowId
            """,
            """
            SELECT RowId, Id, TicketKey, JiraKey, Title, Status, Type,
                   Priority, Resolution, ResolutionDescriptionPlain,
                   WorkGroup, WorkGroupClean, Specification, UpdatedAt,
                   Url, DescriptionHtml, ResolutionDescriptionHtml,
                   CreatedAt, RelatedArtifactsRaw, RelatedPagesRaw,
                   HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_jira_hydration ORDER BY RowId
            """,
            "SELECT * FROM prepared_zulip_hydration ORDER BY RowId",
            "SELECT * FROM prepared_github_hydration ORDER BY RowId",
            "SELECT * FROM prepared_repo_hydration ORDER BY RowId",
            "SELECT * FROM prepared_ticket_jira_xref ORDER BY RowId",
            "SELECT * FROM prepared_ticket_jira_content ORDER BY RowId",
            "SELECT * FROM prepared_ticket_artifacts ORDER BY RowId",
            "SELECT * FROM prepared_ticket_pages ORDER BY RowId",
            "SELECT * FROM prepared_ticket_topics ORDER BY RowId",
            "SELECT * FROM prepared_ticket_topic_groups ORDER BY RowId",
            "SELECT * FROM prepared_ticket_topic_members ORDER BY RowId",
            "SELECT * FROM prepared_ticket_authoring_state ORDER BY TicketKey",
            "SELECT * FROM authoring_result_receipts ORDER BY RowId",
            "SELECT * FROM prepared_ticket_partition_receipts ORDER BY RunId, PartitionKey",
        ];
        using SqliteConnection connection =
            database.Database.OpenConnection();
        return string.Join(
            "\n--query--\n",
            queries.Select(query => DumpRows(connection, query)));
    }

    private static string ReadPublicationFields(TestDatabase database)
    {
        string[] queries =
        [
            """
            SELECT TicketKey, Reporter, Assignee,
                   PublicDisplayNamePolicyVersion, SourceProject,
                   SourceLastSuccessfulRefreshAt, SourceContentRevision,
                   HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_ticket_hydration ORDER BY TicketKey
            """,
            """
            SELECT TicketKey, JiraKey, Reporter, Assignee,
                   PublicDisplayNamePolicyVersion
            FROM prepared_jira_hydration
            ORDER BY TicketKey, JiraKey
            """,
            """
            SELECT TicketKey, DisplayName, PublicDisplayNamePolicyVersion
            FROM prepared_ticket_in_person_requesters
            ORDER BY TicketKey, DisplayName
            """,
            """
            SELECT RunId, Source, LatestSuccessfulRefreshAt,
                   ContentRevision, CapturedAt
            FROM authoring_run_input_provenance
            ORDER BY RunId, Source, RowId
            """,
        ];
        using SqliteConnection connection =
            database.Database.OpenConnection();
        return string.Join(
            "\n--query--\n",
            queries.Select(query => DumpRows(connection, query)));
    }

    private static string DumpRows(
        SqliteConnection connection,
        string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read())
        {
            string[] values = new string[reader.FieldCount];
            for (int index = 0; index < reader.FieldCount; index++)
            {
                values[index] = reader.IsDBNull(index)
                    ? "<null>"
                    : Convert.ToString(
                        reader.GetValue(index),
                        System.Globalization.CultureInfo.InvariantCulture)
                        ?? string.Empty;
            }
            rows.Add(string.Join("\u001f", values));
        }
        return string.Join("\n", rows);
    }

    private sealed record PublicationRefreshContext(
        string SourceRunId,
        string RefreshRunId,
        PreparedTicketPublicationRefreshInventory Inventory,
        string InputFingerprint,
        AuthoringRunStageLease Lease);

    private static JiraIssueSummaryEntry SourceTicket(string key)
        => new()
        {
            Key = key,
            ProjectKey = "FHIR",
            Title = $"Title {key}",
            Type = "Change Request",
            Status = "Triaged",
            WorkGroup = "FHIR-I",
            Specification = "FHIR",
            UpdatedAt =
                new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        };

    private static int ScalarInt(
        SqliteConnection connection,
        string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static async Task SeedHydrationRowAsyncShimToAvoidNameClash(
        PreparerDatabase database, string ticketKey, string jiraKey, string workGroup, string type, string specification)
        => await SeedHydrationRowAsync(database, ticketKey, jiraKey, workGroup, type, specification);

    private static PreparedTicketHydrationBatch SampleBatch(string ticketKey, string jiraKey = "FHIR-100")
    {
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        return new PreparedTicketHydrationBatch(
            TicketKey: ticketKey,
            Parent: SampleParent(ticketKey, hydratedAt),
            JiraRows: [SampleJiraRow(ticketKey, jiraKey, hydratedAt)],
            ZulipRows: [SampleZulipRow(ticketKey, "implementers:ballot", hydratedAt)],
            GitHubRows: [SampleGitHubRow(ticketKey, "HL7/fhir#1", hydratedAt)],
            RepoRows: [SampleRepoRow(ticketKey, "HL7/fhir", hydratedAt)],
            JiraXrefRows: [new PreparedTicketJiraXrefRow(ticketKey, "FHIR-9999", "RelatedIssues")],
            InPersonRequesters:
            [
                new(
                    ticketKey,
                    "Ada Example",
                    PublicDisplayNamePolicy.CurrentVersion),
                new(
                    ticketKey,
                    "Grace Example",
                    PublicDisplayNamePolicy.CurrentVersion),
            ]);
    }

    private static HydrationBatch SampleNeutralBatch(
        string ticketKey,
        int? publicDisplayNamePolicyVersion)
    {
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        return new HydrationBatch(
            TicketKey: ticketKey,
            Parent: new HydrationTicketRow(
                TicketKey: ticketKey,
                Priority: null,
                Resolution: null,
                ResolutionDescriptionPlain: null,
                Specification: "FHIR",
                RaisedInVersion: null,
                SelectedBallot: null,
                ChangeCategory: null,
                Impact: null,
                Labels: null,
                CommentCount: null,
                DescriptionPlain: null,
                HydratedAt: hydratedAt,
                HydrationStatus: "resolved",
                HydrationReason: null,
                Assignee: "Parent Assignee",
                InPersonRequesters: ["Requester Person"],
                StructuredReporter: "Parent Reporter",
                PublicDisplayNamePolicyVersion:
                    publicDisplayNamePolicyVersion),
            JiraRows:
            [
                new HydrationJiraRow(
                    TicketKey: ticketKey,
                    JiraKey: ticketKey,
                    Title: "title",
                    Status: "Open",
                    Type: "Change Request",
                    Priority: null,
                    Resolution: null,
                    ResolutionDescriptionPlain: null,
                    WorkGroup: "FHIR-I",
                    Specification: "FHIR",
                    UpdatedAt: hydratedAt,
                    Url: $"https://jira.example.com/browse/{ticketKey}",
                    HydratedAt: hydratedAt,
                    HydrationStatus: "resolved",
                    HydrationReason: null,
                    Assignee: "Jira Assignee",
                    StructuredReporter: "Jira Reporter",
                    PublicDisplayNamePolicyVersion:
                        publicDisplayNamePolicyVersion),
            ],
            ZulipRows: [],
            GitHubRows: [],
            RepoRows: [],
            JiraXrefRows: []);
    }

    private static PreparedTicketHydrationRow SampleParent(string ticketKey, DateTimeOffset hydratedAt) =>
        new(
            TicketKey: ticketKey,
            Priority: "Major",
            Resolution: "Persuasive",
            ResolutionDescriptionPlain: "done",
            Specification: "FHIR",
            RaisedInVersion: "5.0.0",
            SelectedBallot: "2026-Jan",
            ChangeCategory: "Refinement",
            Impact: "Compatible, substantive",
            Labels: null,
            CommentCount: 3,
            DescriptionPlain: "body text",
            HydratedAt: hydratedAt,
            HydrationStatus: "resolved",
            HydrationReason: null);

    private static PreparedJiraHydrationRow SampleJiraRow(string ticketKey, string jiraKey, DateTimeOffset hydratedAt) =>
        new(
            TicketKey: ticketKey,
            JiraKey: jiraKey,
            Title: "title",
            Status: "Open",
            Type: "Change Request",
            Priority: "Major",
            Resolution: null,
            ResolutionDescriptionPlain: null,
            WorkGroup: "FHIR-I",
            Specification: "FHIR",
            UpdatedAt: hydratedAt,
            Url: $"https://jira.example.com/browse/{jiraKey}",
            HydratedAt: hydratedAt,
            HydrationStatus: "resolved",
            HydrationReason: null);

    private static PreparedZulipHydrationRow SampleZulipRow(string ticketKey, string threadId, DateTimeOffset hydratedAt) =>
        new(
            TicketKey: ticketKey,
            ZulipThreadId: threadId,
            StreamId: 42,
            StreamName: "implementers",
            Topic: "ballot",
            MessageCount: 3,
            FirstMessageAt: hydratedAt,
            LastMessageAt: hydratedAt,
            FirstMessageExcerpt: "first",
            Url: "https://chat.example.com/",
            HydratedAt: hydratedAt,
            HydrationStatus: "resolved",
            HydrationReason: null);

    private static PreparedGitHubHydrationRow SampleGitHubRow(string ticketKey, string itemId, DateTimeOffset hydratedAt) =>
        new(
            TicketKey: ticketKey,
            GitHubItemId: itemId,
            Owner: "HL7",
            Repo: "fhir",
            Number: 1,
            Path: null,
            Title: "title",
            State: "open",
            IsPullRequest: false,
            Labels: null,
            UpdatedAt: hydratedAt,
            Url: $"https://github.com/{itemId}",
            HydratedAt: hydratedAt,
            HydrationStatus: "resolved",
            HydrationReason: null);

    private static PreparedRepoHydrationRow SampleRepoRow(string ticketKey, string repo, DateTimeOffset hydratedAt) =>
        new(
            TicketKey: ticketKey,
            Repo: repo,
            Description: "FHIR core spec",
            WorkGroup: null,
            Specification: null,
            CategoryDetail: "FhirCore",
            Url: $"https://github.com/{repo}",
            HydratedAt: hydratedAt,
            HydrationStatus: "resolved",
            HydrationReason: null);

    private static int CountWhere(TestDatabase database, string table, string whereClause)
    {
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {whereClause}";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static TestDatabase CreateDatabase()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "preparer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preparer.db");
        PreparerDatabase database = new(path, NullLogger<PreparerDatabase>.Instance);
        database.Initialize();
        return new TestDatabase(directory, database);
    }

    private static PreparedTicketPayload SamplePayload(string key) => new()
    {
        Key = key,
        RequestSummary = "request",
        CommentSummary = "comments",
        LinkedTicketSummary = "linked",
        RelatedTicketSummary = "related",
        RelatedZulipSummary = "zulip",
        RelatedGitHubSummary = "github",
        ExistingProposed = "existing",
        ProposalA = "proposal a",
        ProposalAJustification = "why a",
        ProposalAImpact = "Non-substantive",
        ProposalB = "proposal b",
        ProposalBJustification = "why b",
        ProposalBImpact = "Compatible, substantive",
        ProposalC = "proposal c",
        ProposalCJustification = "why c",
        Recommendation = "A",
        RecommendationJustification = "because",
        SavedAt = DateTimeOffset.Parse("2026-04-29T00:00:00Z"),
        Repos = [new PreparedTicketRepoPayload { Repo = "HL7/fhir", RepoCategory = "FHIR Core", Justification = "repo" }],
        RelatedJiraTickets = [new PreparedTicketRelatedJiraPayload { AssociatedTicketKey = "FHIR-999", LinkType = "related", Justification = "jira" }],
        RelatedZulipThreads = [new PreparedTicketRelatedZulipPayload { ZulipThreadId = "123", Justification = "zulip" }],
        RelatedGitHubItems = [new PreparedTicketRelatedGitHubPayload { GitHubItemId = "HL7/fhir#1", Justification = "github" }],
    };

    private static bool Exists(TestDatabase database, string type, string name)
    {
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = @type AND name = @name";
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@name", name);
        return command.ExecuteScalar() is not null;
    }

    private static bool IsRowIdPrimaryKey(TestDatabase database, string table)
    {
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string columnName = reader.GetString(reader.GetOrdinal("name"));
            int pk = reader.GetInt32(reader.GetOrdinal("pk"));
            if (string.Equals(columnName, "RowId", StringComparison.Ordinal))
            {
                return pk == 1;
            }
        }
        return false;
    }

    private static bool HasUniqueIndexOver(TestDatabase database, string table, string column)
        => HasUniqueIndexOverColumns(database, table, [column]);

    private static bool HasUniqueIndexOverColumns(TestDatabase database, string table, IReadOnlyList<string> expectedColumns)
    {
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand listCommand = connection.CreateCommand();
        listCommand.CommandText = $"PRAGMA index_list({table})";
        List<(string Name, bool Unique)> indexes = [];
        using (SqliteDataReader reader = listCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                string name = reader.GetString(reader.GetOrdinal("name"));
                long unique = reader.GetInt64(reader.GetOrdinal("unique"));
                indexes.Add((name, unique == 1));
            }
        }
        foreach ((string name, bool unique) in indexes)
        {
            if (!unique)
            {
                continue;
            }
            using SqliteCommand info = connection.CreateCommand();
            info.CommandText = $"PRAGMA index_info({name})";
            using SqliteDataReader r = info.ExecuteReader();
            List<string> indexColumns = [];
            while (r.Read())
            {
                indexColumns.Add(r.GetString(r.GetOrdinal("name")));
            }
            if (indexColumns.Count != expectedColumns.Count)
            {
                continue;
            }
            bool allMatch = true;
            for (int i = 0; i < indexColumns.Count; i++)
            {
                if (!string.Equals(indexColumns[i], expectedColumns[i], StringComparison.Ordinal))
                {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch)
            {
                return true;
            }
        }
        return false;
    }

    private static int Count(TestDatabase database, string table)
    {
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    [Fact]
    public void Initialize_HydrationWorkGroupCleanIndex_Exists()
    {
        using TestDatabase database = CreateDatabase();
        Assert.True(HasIndexOver(database, "prepared_jira_hydration", "WorkGroupClean"));
    }

    [Fact]
    public async Task InsertJiraHydration_PopulatesWorkGroupClean_FromCleaner()
    {
        using TestDatabase database = CreateDatabase();

        await using (SqliteConnection conn = database.Database.OpenConnection())
        await using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO prepared_ticket_hydration (Id, TicketKey, HydratedAt, HydrationStatus) VALUES (@id, 'FHIR-1', @at, 'resolved')";
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        await SeedHydrationRowAsync(
            database.Database,
            ticketKey: "FHIR-1",
            jiraKey: "FHIR-1",
            workGroup: "Orders & Observations",
            type: "Change Request",
            specification: "FHIR Core");

        await using SqliteConnection check = database.Database.OpenConnection();
        await using SqliteCommand readCmd = check.CreateCommand();
        readCmd.CommandText = "SELECT WorkGroupClean FROM prepared_jira_hydration WHERE TicketKey='FHIR-1'";
        object? scalar = await readCmd.ExecuteScalarAsync();
        Assert.Equal("OrdersAndObservations", scalar);
    }

    [Fact]
    public async Task BackfillJiraHydrationWorkGroupClean_v1_PopulatesAndIsIdempotent()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "preparer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string dbPath = Path.Combine(directory, "preparer.db");

        PreparerDatabase db1 = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        db1.Initialize();

        // Simulate a pre-migration row: stored WorkGroupClean is NULL.
        await using (SqliteConnection seed = db1.OpenConnection())
        {
            await using SqliteCommand insert = seed.CreateCommand();
            insert.CommandText = """
                INSERT INTO prepared_jira_hydration
                (Id, TicketKey, JiraKey, Title, Status, Type, WorkGroup, WorkGroupClean, HydratedAt, HydrationStatus)
                VALUES (@id, 'FHIR-1', 'FHIR-1', 'title', 'Open', 'CR', 'Orders & Observations', NULL, @at, 'resolved')
                """;
            insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();

            // Force the migration to re-run by deleting its sentinel.
            await using SqliteCommand delSentinel = seed.CreateCommand();
            delSentinel.CommandText = "DELETE FROM schema_migrations WHERE Name = 'prepared-jira-hydration-clean-v1'";
            await delSentinel.ExecuteNonQueryAsync();
        }
        db1.Dispose();

        // Re-open: EnsureSchema runs, sentinel is missing, backfill runs.
        PreparerDatabase db2 = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        db2.Initialize();
        try
        {
            await using SqliteConnection check = db2.OpenConnection();
            await using SqliteCommand readCmd = check.CreateCommand();
            readCmd.CommandText = "SELECT WorkGroupClean FROM prepared_jira_hydration WHERE TicketKey='FHIR-1'";
            object? scalar = await readCmd.ExecuteScalarAsync();
            Assert.Equal("OrdersAndObservations", scalar);

            await using SqliteCommand sentinelCmd = check.CreateCommand();
            sentinelCmd.CommandText = "SELECT 1 FROM schema_migrations WHERE Name = 'prepared-jira-hydration-clean-v1'";
            Assert.NotNull(await sentinelCmd.ExecuteScalarAsync());
        }
        finally
        {
            db2.Dispose();
        }

        // Open a third time — sentinel is now present, migration is a no-op.
        PreparerDatabase db3 = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        db3.Initialize();
        try
        {
            await using SqliteConnection check = db3.OpenConnection();
            await using SqliteCommand readCmd = check.CreateCommand();
            readCmd.CommandText = "SELECT WorkGroupClean FROM prepared_jira_hydration WHERE TicketKey='FHIR-1'";
            object? scalar = await readCmd.ExecuteScalarAsync();
            Assert.Equal("OrdersAndObservations", scalar);
        }
        finally
        {
            db3.Dispose();
        }

        TestFileCleanup.SafeDeleteDirectory(directory);
    }

    [Fact]
    public void Initialize_OnLegacyHydrationSchema_MissingWorkGroupClean_AddsColumnAndIndex()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "preparer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string dbPath = Path.Combine(directory, "preparer.db");

        // Build a fresh schema, then simulate a pre-WorkGroupClean legacy DB by
        // dropping the column and its index from prepared_jira_hydration.
        PreparerDatabase fresh = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        fresh.Initialize();
        using (SqliteConnection conn = fresh.OpenConnection())
        using (SqliteCommand drop = conn.CreateCommand())
        {
            drop.CommandText = """
                DROP INDEX IF EXISTS IDX_prepared_jira_hydration_WorkGroupClean;
                ALTER TABLE prepared_jira_hydration DROP COLUMN WorkGroupClean;
                """;
            drop.ExecuteNonQuery();
        }
        fresh.Dispose();

        // Re-initialising must NOT throw. The generated CreateTable builds an
        // index over WorkGroupClean; under DQS-off SQLite (SourceGear) the column
        // must be re-added before that index is built. Regression guard for the
        // migrate-before-CreateTable ordering.
        PreparerDatabase upgraded = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        upgraded.Initialize();
        try
        {
            using SqliteConnection conn = upgraded.OpenConnection();
            using SqliteCommand info = conn.CreateCommand();
            info.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('prepared_jira_hydration') WHERE name = 'WorkGroupClean'";
            Assert.Equal(1, Convert.ToInt32(info.ExecuteScalar()));
        }
        finally
        {
            upgraded.Dispose();
        }

        TestFileCleanup.SafeDeleteDirectory(directory);
    }

    private static bool HasIndexOver(TestDatabase database, string table, string column)
    {
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand list = connection.CreateCommand();
        list.CommandText = $"PRAGMA index_list({table})";
        List<string> names = [];
        using (SqliteDataReader reader = list.ExecuteReader())
        {
            while (reader.Read()) names.Add(reader.GetString(reader.GetOrdinal("name")));
        }
        foreach (string name in names)
        {
            using SqliteCommand info = connection.CreateCommand();
            info.CommandText = $"PRAGMA index_info({name})";
            using SqliteDataReader r = info.ExecuteReader();
            while (r.Read())
            {
                if (string.Equals(r.GetString(r.GetOrdinal("name")), column, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    [Fact]
    public async Task BackfillTicketTopicsWorkGroupClean_v1_HappyPath_ReslugsAndIsIdempotent()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "preparer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string dbPath = Path.Combine(directory, "preparer.db");

        PreparerDatabase db1 = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        db1.Initialize();

        await using (SqliteConnection seed = db1.OpenConnection())
        {
            await using SqliteCommand insert = seed.CreateCommand();
            insert.CommandText = """
                INSERT INTO prepared_ticket_topics
                (Id, WorkGroupClean, WorkGroupDisplay, Specification, Type, ShortDescription, LongerDescription, RenderOrderHint, SavedAt)
                VALUES (@id, 'Orders&Observations', 'Orders & Observations', 'FHIR Core', 'Change Request', 'Topic A', 'desc', NULL, @at)
                """;
            insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();

            await using SqliteCommand delSentinel = seed.CreateCommand();
            delSentinel.CommandText = "DELETE FROM schema_migrations WHERE Name = 'ticket-topics-clean-v1'";
            await delSentinel.ExecuteNonQueryAsync();
        }
        db1.Dispose();

        PreparerDatabase db2 = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        db2.Initialize();
        try
        {
            await using SqliteConnection check = db2.OpenConnection();
            await using SqliteCommand readCmd = check.CreateCommand();
            readCmd.CommandText = "SELECT WorkGroupClean FROM prepared_ticket_topics LIMIT 1";
            object? scalar = await readCmd.ExecuteScalarAsync();
            Assert.Equal("OrdersAndObservations", scalar);

            await using SqliteCommand sentinelCmd = check.CreateCommand();
            sentinelCmd.CommandText = "SELECT 1 FROM schema_migrations WHERE Name = 'ticket-topics-clean-v1'";
            Assert.NotNull(await sentinelCmd.ExecuteScalarAsync());
        }
        finally
        {
            db2.Dispose();
        }

        // Re-open: sentinel present, migration is a no-op.
        PreparerDatabase db3 = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        db3.Initialize();
        try
        {
            await using SqliteConnection check = db3.OpenConnection();
            await using SqliteCommand readCmd = check.CreateCommand();
            readCmd.CommandText = "SELECT WorkGroupClean FROM prepared_ticket_topics LIMIT 1";
            object? scalar = await readCmd.ExecuteScalarAsync();
            Assert.Equal("OrdersAndObservations", scalar);
        }
        finally
        {
            db3.Dispose();
        }

        TestFileCleanup.SafeDeleteDirectory(directory);
    }

    [Fact]
    public async Task BackfillTicketTopicsWorkGroupClean_v1_AllowsUniqueSlugSwap()
    {
        string directory = Path.Combine(
            Environment.CurrentDirectory,
            "temp",
            "preparer-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string dbPath = Path.Combine(directory, "preparer.db");
        PreparerDatabase db1 = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        db1.Initialize();
        await using (SqliteConnection seed = db1.OpenConnection())
        {
            await using SqliteCommand insert = seed.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO prepared_ticket_topics
                    (Id, WorkGroupClean, WorkGroupDisplay, Specification, Type, ShortDescription, LongerDescription, RenderOrderHint, SavedAt)
                VALUES
                    (@id1, 'PatientAdministration', 'Orders & Observations', 'FHIR Core', 'Change Request', 'Topic A', 'one', NULL, @at),
                    (@id2, 'OrdersAndObservations', 'Patient Administration', 'FHIR Core', 'Change Request', 'Topic A', 'two', NULL, @at);
                DELETE FROM schema_migrations WHERE Name = 'ticket-topics-clean-v1';
                """;
            insert.Parameters.AddWithValue("@id1", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("@id2", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        db1.Dispose();

        PreparerDatabase db2 = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        db2.Initialize();
        try
        {
            await using SqliteConnection check = db2.OpenConnection();
            await using SqliteCommand command = check.CreateCommand();
            command.CommandText =
                "SELECT WorkGroupClean FROM prepared_ticket_topics WHERE WorkGroupDisplay = 'Orders & Observations'";
            Assert.Equal("OrdersAndObservations", await command.ExecuteScalarAsync());
            command.CommandText =
                "SELECT WorkGroupClean FROM prepared_ticket_topics WHERE WorkGroupDisplay = 'Patient Administration'";
            Assert.Equal("PatientAdministration", await command.ExecuteScalarAsync());
        }
        finally
        {
            db2.Dispose();
            TestFileCleanup.SafeDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task BackfillTicketTopicsWorkGroupClean_v1_CollisionPath_Aborts()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "preparer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string dbPath = Path.Combine(directory, "preparer.db");

        PreparerDatabase db1 = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        db1.Initialize();

        // Two rows whose existing WorkGroupClean differ but whose reslug
        // target (cleaner over WorkGroupDisplay) would collapse onto the
        // same (Clean, Spec, Type, Short) tuple. The pre-migration state
        // is reachable because the existing WorkGroupClean values are
        // different.
        await using (SqliteConnection seed = db1.OpenConnection())
        {
            await using SqliteCommand i1 = seed.CreateCommand();
            i1.CommandText = """
                INSERT INTO prepared_ticket_topics
                (Id, WorkGroupClean, WorkGroupDisplay, Specification, Type, ShortDescription, LongerDescription, RenderOrderHint, SavedAt)
                VALUES (@id, 'OrdersAndObservations', 'Orders & Observations', 'FHIR Core', 'Change Request', 'Topic A', 'd1', NULL, @at)
                """;
            i1.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            i1.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            await i1.ExecuteNonQueryAsync();

            await using SqliteCommand i2 = seed.CreateCommand();
            i2.CommandText = """
                INSERT INTO prepared_ticket_topics
                (Id, WorkGroupClean, WorkGroupDisplay, Specification, Type, ShortDescription, LongerDescription, RenderOrderHint, SavedAt)
                VALUES (@id, 'Orders_And_Observations', 'Orders & Observations', 'FHIR Core', 'Change Request', 'Topic A', 'd2', NULL, @at)
                """;
            i2.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
            i2.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            await i2.ExecuteNonQueryAsync();

            await using SqliteCommand delSentinel = seed.CreateCommand();
            delSentinel.CommandText = "DELETE FROM schema_migrations WHERE Name = 'ticket-topics-clean-v1'";
            await delSentinel.ExecuteNonQueryAsync();
        }
        db1.Dispose();

        PreparerDatabase db2 = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        Assert.Throws<WorkGroupCleanReslugAbortedException>(() => db2.Initialize());
        db2.Dispose();

        // Re-open with fresh handle to confirm sentinel was NOT written.
        // We can't re-run Initialize on the same db that threw; open a new one
        // and inspect the table directly.
        PreparerDatabase db3 = new(dbPath, NullLogger<PreparerDatabase>.Instance);
        // Initialize will throw again because the duplicate still exists.
        Assert.Throws<WorkGroupCleanReslugAbortedException>(() => db3.Initialize());
        db3.Dispose();

        TestFileCleanup.SafeDeleteDirectory(directory);
    }

    private sealed class TestDatabase(string directory, PreparerDatabase database) : IDisposable
    {
        public PreparerDatabase Database { get; } = database;
        public string Directory { get; } = directory;

        public void Dispose()
        {
            Database.Dispose();
            TestFileCleanup.SafeDeleteDirectory(Directory);
        }
    }
}
