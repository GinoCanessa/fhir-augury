using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketGroupingPersistenceTests
{
    private const string WorkGroupClean = "OrdersAndObservations";
    private const string WorkGroupDisplay = "Orders and Observations";
    private const string Specification = "FHIR Core";
    private const string Type = "Change Request";

    [Fact]
    public async Task SaveGrouping_InsertsTopicsGroupsAndMembers()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-50");

        PreparedTicketGroupingPayload payload = SamplePayload();

        PreparedTicketGroupingSaveResult result = await database.Database.SaveGroupingAsync(payload);

        Assert.Equal(1, Count(database, "prepared_ticket_topics"));
        Assert.Equal(1, Count(database, "prepared_ticket_topic_groups"));
        Assert.Equal(3, Count(database, "prepared_ticket_topic_members"));
        Assert.Equal(1, result.TopicRows);
        Assert.Equal(1, result.TopicGroupRows);
        Assert.Equal(3, result.MemberRows);
    }

    [Fact]
    public async Task SaveGrouping_ReplacesPartitionAtomically()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-50");
        await SeedPreparedTicketAsync(database, "FHIR-3");
        await SeedPreparedTicketAsync(database, "FHIR-4");
        await SeedPreparedTicketAsync(database, "FHIR-200");
        await SeedPreparedTicketAsync(database, "FHIR-201");

        // Neighbouring partition uses disjoint ticket keys so we can prove the
        // replacement only touches the target partition's rows.
        PreparedTicketGroupingPayload neighbour = SamplePayload();
        neighbour.Type = "Technical Correction";
        neighbour.Topics[0].LinkedTicketGroups[0].FirstTicketKey = "FHIR-200";
        neighbour.Topics[0].LinkedTicketGroups[0].Members =
        [
            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-200", Order = 0 },
            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-201", Order = 1 },
        ];
        neighbour.Topics[0].RemainingTicketKeys = [];
        await database.Database.SaveGroupingAsync(neighbour);

        await database.Database.SaveGroupingAsync(SamplePayload());

        PreparedTicketGroupingPayload replacement = SamplePayload();
        replacement.Topics[0].ShortDescription = "Replaced description";
        replacement.Topics[0].LinkedTicketGroups[0].Members =
        [
            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-3", Order = 0 },
            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-4", Order = 1 },
        ];
        replacement.Topics[0].LinkedTicketGroups[0].FirstTicketKey = "FHIR-3";
        replacement.Topics[0].RemainingTicketKeys = [];
        await database.Database.SaveGroupingAsync(replacement);

        Assert.Equal(2, Count(database, "prepared_ticket_topics"));
        Assert.Equal(2, Count(database, "prepared_ticket_topic_groups"));
        Assert.Equal(0, CountWhere(database, "prepared_ticket_topic_members", "TicketKey = 'FHIR-1'"));
        Assert.Equal(1, CountWhere(database, "prepared_ticket_topic_members", "TicketKey = 'FHIR-3'"));
        Assert.Equal(1, CountWhere(database, "prepared_ticket_topic_members", "TicketKey = 'FHIR-200'"));
        Assert.Equal(1, CountWhere(
            database,
            "prepared_ticket_topics",
            $"WorkGroupClean = '{WorkGroupClean}' AND Specification = '{Specification}' AND Type = 'Technical Correction'"));
    }

    [Fact]
    public async Task SaveGrouping_RejectsUnknownTicketKeys()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");

        PreparedTicketGroupingPayload payload = SamplePayload();

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() => database.Database.SaveGroupingAsync(payload));
        Assert.Contains("FHIR-2", ex.Message);

        Assert.Equal(0, Count(database, "prepared_ticket_topics"));
        Assert.Equal(0, Count(database, "prepared_ticket_topic_groups"));
        Assert.Equal(0, Count(database, "prepared_ticket_topic_members"));
    }

    [Fact]
    public async Task SaveGrouping_RejectsSingletonTopic()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");

        PreparedTicketGroupingPayload payload = SamplePayload();
        payload.Topics[0].LinkedTicketGroups.Clear();
        payload.Topics[0].RemainingTicketKeys = ["FHIR-1"];

        await Assert.ThrowsAsync<ArgumentException>(() => database.Database.SaveGroupingAsync(payload));

        Assert.Equal(0, Count(database, "prepared_ticket_topics"));
        Assert.Equal(0, Count(database, "prepared_ticket_topic_members"));
    }

    [Fact]
    public async Task SaveGrouping_RejectsDuplicateTicketAcrossTopicsInPartition()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-50");

        PreparedTicketGroupingPayload payload = SamplePayload();
        payload.Topics.Add(new PreparedTicketTopicPayload
        {
            ShortDescription = "Other topic",
            LongerDescription = "Other longer.",
            RemainingTicketKeys = ["FHIR-1", "FHIR-50"],
        });

        await Assert.ThrowsAsync<ArgumentException>(() => database.Database.SaveGroupingAsync(payload));

        Assert.Equal(0, Count(database, "prepared_ticket_topics"));
        Assert.Equal(0, Count(database, "prepared_ticket_topic_members"));
    }

    [Fact]
    public async Task GetGrouping_RendersHintedTopicsBeforeNullHintTopics()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-3");
        await SeedPreparedTicketAsync(database, "FHIR-4");
        await SeedPreparedTicketAsync(database, "FHIR-5");

        PreparedTicketGroupingPayload payload = SamplePayload();
        payload.Topics[0].RemainingTicketKeys = [];
        payload.Topics[0].RenderOrderHint = 5;
        payload.Topics[0].ShortDescription = "Hinted topic";
        payload.Topics[0].LinkedTicketGroups[0].Members =
        [
            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-1", Order = 0 },
            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-2", Order = 1 },
        ];
        payload.Topics[0].LinkedTicketGroups[0].FirstTicketKey = "FHIR-1";
        payload.Topics.Add(new PreparedTicketTopicPayload
        {
            ShortDescription = "Unhinted topic with more members",
            LongerDescription = "Has more total members but no hint.",
            RenderOrderHint = null,
            RemainingTicketKeys = ["FHIR-3", "FHIR-4", "FHIR-5"],
        });

        await database.Database.SaveGroupingAsync(payload);

        PreparedTicketGroupingPartition? partition = await database.Database.GetGroupingAsync(WorkGroupClean, Specification, Type);
        Assert.NotNull(partition);
        Assert.Equal(2, partition!.Topics.Count);
        Assert.Equal("Hinted topic", partition.Topics[0].ShortDescription);
        Assert.Equal("Unhinted topic with more members", partition.Topics[1].ShortDescription);
    }

    [Fact]
    public async Task GetGrouping_NullHintTopicsSortDescendingByMemberCountThenName()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-3");
        await SeedPreparedTicketAsync(database, "FHIR-4");
        await SeedPreparedTicketAsync(database, "FHIR-5");
        await SeedPreparedTicketAsync(database, "FHIR-6");
        await SeedPreparedTicketAsync(database, "FHIR-7");
        await SeedPreparedTicketAsync(database, "FHIR-8");
        await SeedPreparedTicketAsync(database, "FHIR-9");
        await SeedPreparedTicketAsync(database, "FHIR-10");
        await SeedPreparedTicketAsync(database, "FHIR-11");

        PreparedTicketGroupingPayload payload = new()
        {
            WorkGroupClean = WorkGroupClean,
            WorkGroupDisplay = WorkGroupDisplay,
            Specification = Specification,
            Type = Type,
            Topics =
            [
                new PreparedTicketTopicPayload
                {
                    ShortDescription = "Charlie",
                    LongerDescription = "third alphabetically",
                    RenderOrderHint = null,
                    RemainingTicketKeys = ["FHIR-1", "FHIR-2", "FHIR-3"],
                },
                new PreparedTicketTopicPayload
                {
                    ShortDescription = "Bravo",
                    LongerDescription = "second alphabetically",
                    RenderOrderHint = null,
                    RemainingTicketKeys = ["FHIR-4", "FHIR-5", "FHIR-6"],
                },
                new PreparedTicketTopicPayload
                {
                    ShortDescription = "Alpha big",
                    LongerDescription = "biggest by count",
                    RenderOrderHint = null,
                    RemainingTicketKeys = ["FHIR-7", "FHIR-8", "FHIR-9", "FHIR-10", "FHIR-11"],
                },
            ],
        };

        await database.Database.SaveGroupingAsync(payload);

        PreparedTicketGroupingPartition partition = (await database.Database.GetGroupingAsync(WorkGroupClean, Specification, Type))!;
        Assert.Equal(3, partition.Topics.Count);
        Assert.Equal("Alpha big", partition.Topics[0].ShortDescription);
        Assert.Equal("Bravo", partition.Topics[1].ShortDescription);
        Assert.Equal("Charlie", partition.Topics[2].ShortDescription);
    }

    [Fact]
    public async Task GetGrouping_ComputesIndividualTicketsViaPartitionPredicate()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-3");
        await SeedHydrationSelfAsync(database, "FHIR-1", WorkGroupDisplay, Type, Specification);
        await SeedHydrationSelfAsync(database, "FHIR-2", "Other Workgroup", Type, Specification);
        await SeedHydrationSelfAsync(database, "FHIR-3", WorkGroupDisplay, "Comment", Specification);

        PreparedTicketGroupingPayload payload = new()
        {
            WorkGroupClean = WorkGroupClean,
            WorkGroupDisplay = WorkGroupDisplay,
            Specification = Specification,
            Type = Type,
            Topics =
            [
                new PreparedTicketTopicPayload
                {
                    ShortDescription = "Has FHIR-1",
                    LongerDescription = "places FHIR-1 in a topic",
                    RemainingTicketKeys = ["FHIR-1", "FHIR-99"],
                },
            ],
        };
        await SeedPreparedTicketAsync(database, "FHIR-99");
        await database.Database.SaveGroupingAsync(payload);

        PreparedTicketGroupingPartition partition = (await database.Database.GetGroupingAsync(WorkGroupClean, Specification, Type))!;
        Assert.Empty(partition.IndividualTicketKeys);

        PreparedTicketGroupingPartition? commentPartition = await database.Database.GetGroupingAsync(WorkGroupClean, Specification, "Comment");
        Assert.NotNull(commentPartition);
        Assert.Single(commentPartition!.IndividualTicketKeys);
        Assert.Equal("FHIR-3", commentPartition.IndividualTicketKeys[0]);
    }

    [Fact]
    public async Task GetGrouping_ExcludesUnhydratedTicketsAndReportsCount()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedHydrationSelfAsync(database, "FHIR-1", WorkGroupDisplay, Type, Specification);
        // FHIR-2 has no self-hydration row.

        PreparedTicketGroupingPayload payload = new()
        {
            WorkGroupClean = WorkGroupClean,
            WorkGroupDisplay = WorkGroupDisplay,
            Specification = Specification,
            Type = Type,
            Topics =
            [
                new PreparedTicketTopicPayload
                {
                    ShortDescription = "topic",
                    LongerDescription = "longer",
                    RemainingTicketKeys = ["FHIR-1", "FHIR-2"],
                },
            ],
        };

        await database.Database.SaveGroupingAsync(payload);
        PreparedTicketGroupingPartition partition = (await database.Database.GetGroupingAsync(WorkGroupClean, Specification, Type))!;
        Assert.DoesNotContain("FHIR-2", partition.IndividualTicketKeys);
        Assert.Equal(1, partition.UnattributedTicketCount);
    }

    [Fact]
    public async Task SavePreparedTicket_DeletesGroupingMembersForKey()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-50");

        await database.Database.SaveGroupingAsync(SamplePayload());

        Assert.Equal(1, CountWhere(database, "prepared_ticket_topic_members", "TicketKey = 'FHIR-1'"));

        await SeedPreparedTicketAsync(database, "FHIR-1");

        Assert.Equal(0, CountWhere(database, "prepared_ticket_topic_members", "TicketKey = 'FHIR-1'"));
        Assert.Equal(1, Count(database, "prepared_ticket_topics"));
        Assert.Equal(1, Count(database, "prepared_ticket_topic_groups"));
    }

    [Fact]
    public async Task DeleteGroupingAsync_ClearsPartitionOnly()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-50");

        PreparedTicketGroupingPayload first = SamplePayload();
        PreparedTicketGroupingPayload second = SamplePayload();
        second.Type = "Technical Correction";

        await database.Database.SaveGroupingAsync(first);
        await database.Database.SaveGroupingAsync(second);

        await database.Database.DeleteGroupingAsync(WorkGroupClean, Specification, Type);

        Assert.Equal(1, Count(database, "prepared_ticket_topics"));
        Assert.Equal(1, Count(database, "prepared_ticket_topic_groups"));
        Assert.Equal(3, Count(database, "prepared_ticket_topic_members"));
        Assert.Equal(0, CountWhere(database, "prepared_ticket_topics", $"Type = '{Type}'"));
    }

    [Fact]
    public async Task GetWorkGroupGroupings_DiscoversPartitionsFromBothTablesAndResolvesDisplayName()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-50");
        await SeedPreparedTicketAsync(database, "FHIR-77");
        await SeedHydrationSelfAsync(database, "FHIR-77", WorkGroupDisplay, "Comment", Specification);

        await database.Database.SaveGroupingAsync(SamplePayload());

        PreparedTicketGroupingWorkGroupView? view = await database.Database.GetWorkGroupGroupingsAsync(WorkGroupClean);
        Assert.NotNull(view);
        Assert.Equal(WorkGroupDisplay, view!.WorkGroupDisplay);
        Assert.Equal(2, view.Partitions.Count);
        Assert.Contains(view.Partitions, p => p.Type == Type);
        Assert.Contains(view.Partitions, p => p.Type == "Comment");
    }

    [Fact]
    public async Task SaveGroupingForRun_AtomicallyRecordsFingerprintAndRejectsStaleLease()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-50");
        AuthoringRunStore authoringStore = new(database.Database);
        await authoringStore.EnsureProcessorModeAsync("jira-fhir");
        await authoringStore.TransitionProcessorModeAsync(
            "jira-fhir",
            AuthoringStatusValues.ProcessorModes.Legacy,
            AuthoringStatusValues.ProcessorModes.CuttingOver);
        await authoringStore.TransitionProcessorModeAsync(
            "jira-fhir",
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            AuthoringStatusValues.ProcessorModes.RunBacked);
        AuthoringRunRecord run = await authoringStore.CreateRunAsync(
            "jira-fhir",
            [
                new AuthoringRunItemDefinition("FHIR-1", "fhir", "revision-1"),
                new AuthoringRunItemDefinition("FHIR-2", "fhir", "revision-2"),
                new AuthoringRunItemDefinition("FHIR-50", "fhir", "revision-50"),
            ],
            databaseOnly: true);
        Assert.True(await authoringStore.TryAcquireMutationFenceAsync("jira-fhir", run.Id));
        foreach (AuthoringRunItemRecord item in await authoringStore.GetRunItemsAsync(run.Id))
        {
            AuthoringOperationClaim claim =
                (await authoringStore.ClaimItemAsync(run.Id, item.Id))!;
            PreparedTicketPayload authoredPayload = CreatePreparedTicketPayload(item.BusinessKey);
            string contentHash = AuthoringResultHasher.HashNormalizedUtf8(item.BusinessKey);
            AuthoringReceiptAcceptance receipt = await authoringStore.AcceptResultAsync(
                new AuthoringResultSubmission(
                    run.Id,
                    item.Id,
                    claim.OperationId,
                    item.ExpectedSourceRevision,
                    contentHash),
                claim.OperationToken,
                (connection, ct) => database.Database.SavePreparedTicketForAuthoringAsync(
                    connection,
                    authoredPayload,
                    contentHash,
                    run.Id,
                    item.Id,
                    claim.OperationId,
                    ct));
            await authoringStore.MarkItemCompleteAsync(item.Id, receipt.Receipt.ReceiptId);
            await SeedHydrationSelfAsync(
                database,
                item.BusinessKey,
                WorkGroupDisplay,
                Type,
                Specification);
        }
        string partitionKey = PreparerDatabase.GetPartitionKey(
            WorkGroupClean,
            Specification,
            Type);
        PreparedTicketRunPartition partition = Assert.Single(
            await database.Database.GetRunPartitionsAsync(run.Id));
        string inputFingerprint = partition.InputFingerprint;
        AuthoringRunStageRecord stage = await authoringStore.EnsureRunStageAsync(
            run.Id,
            "grouping",
            partitionKey,
            inputFingerprint);
        AuthoringRunStageLease lease = Assert.IsType<AuthoringRunStageLease>(
            await authoringStore.TryStartRunStageAsync(stage.Id));

        PreparedTicketGroupingsController controller = new(
            database.Database,
            authoringStore);
        ActionResult<PreparedTicketGroupingSaveResultDto> claimed =
            await controller.PutPartition(
                WorkGroupClean,
                Specification,
                Type,
                new PreparedTicketGroupingPutRequest(
                    WorkGroupDisplay,
                    [],
                    new PreparedTicketGroupingStageContext(
                        run.Id,
                        stage.Id,
                        lease.LeaseId,
                        inputFingerprint,
                        partitionKey,
                        inputFingerprint,
                        [],
                        partition.TicketKeys)),
                CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(claimed.Result);

        await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Database.SaveGroupingAsync(SamplePayload()));
        PreparedTicketGroupingPayload wrongPartition = SamplePayload();
        wrongPartition.Type = "Comment";
        await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Database.SaveGroupingForRunAsync(
                wrongPartition,
                run.Id,
                stage.Id,
                lease.LeaseId,
                inputFingerprint));

        PreparedTicketGroupingSaveResult result =
            await database.Database.SaveGroupingForRunAsync(
                SamplePayload(),
                run.Id,
                stage.Id,
                lease.LeaseId,
                inputFingerprint);

        Assert.Equal(1, result.TopicRows);
        Assert.Equal(1, Count(database, "prepared_ticket_partition_receipts"));
        string expectedOutputFingerprint =
            PreparedTicketPublicationContract
                .ComputeGroupingOutputFingerprint(SamplePayload());
        using (SqliteConnection connection =
               database.Database.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT OutputFingerprint
                FROM prepared_ticket_partition_receipts
                WHERE RunId = @runId AND PartitionKey = @partitionKey
                """;
            command.Parameters.AddWithValue("@runId", run.Id);
            command.Parameters.AddWithValue(
                "@partitionKey",
                partitionKey);
            Assert.Equal(
                expectedOutputFingerprint,
                command.ExecuteScalar());
        }
        PreparedTicketGroupingPayload replacement = SamplePayload();
        replacement.Topics = [];
        await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Database.SaveGroupingForRunAsync(
                replacement,
                run.Id,
                stage.Id,
                "stale-lease",
                inputFingerprint));
        Assert.Equal(1, Count(database, "prepared_ticket_topics"));

        using (SqliteConnection connection =
               database.Database.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE prepared_ticket_partition_receipts
                SET OutputFingerprint = NULL
                WHERE RunId = @runId AND PartitionKey = @partitionKey
                """;
            command.Parameters.AddWithValue("@runId", run.Id);
            command.Parameters.AddWithValue(
                "@partitionKey",
                partition.PartitionKey);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        await authoringStore.ReleaseMutationFenceAsync(
            "jira-fhir",
            run.Id);
        string refreshRunId = Guid.NewGuid().ToString("N");
        using (SqliteConnection connection =
               database.Database.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO authoring_runs(
                    Id, ProcessorKind, AuthoringEpoch, Status, Purpose,
                    SourceRunId, DatabaseOnly, TotalItems,
                    CreatedAt, StartedAt)
                SELECT @refreshRunId, ProcessorKind, AuthoringEpoch,
                       'running', 'publication-refresh', Id, 0, 1,
                       @createdAt, @createdAt
                FROM authoring_runs WHERE Id = @sourceRunId;
                INSERT INTO authoring_mutation_fences(
                    ProcessorKind, RunId, LeaseId, AcquiredAt)
                VALUES(
                    'jira-fhir', @refreshRunId, @fenceLeaseId,
                    @createdAt);
                """;
            command.Parameters.AddWithValue(
                "@refreshRunId",
                refreshRunId);
            command.Parameters.AddWithValue("@sourceRunId", run.Id);
            command.Parameters.AddWithValue(
                "@createdAt",
                DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue(
                "@fenceLeaseId",
                Guid.NewGuid().ToString("N"));
            command.ExecuteNonQuery();
        }
        AuthoringRunStageRecord certificationStage =
            await authoringStore.EnsureRunStageAsync(
                refreshRunId,
                PreparerDatabase.GroupingCertificationStageName,
                partition.PartitionKey,
                partition.InputFingerprint);
        AuthoringRunStageLease certificationLease =
            Assert.IsType<AuthoringRunStageLease>(
                await authoringStore.TryStartRunStageAsync(
                    certificationStage.Id));

        PreparedTicketGroupingCertificationEvidence evidence =
            await database.Database.CertifyGroupingPartitionAsync(
                refreshRunId,
                certificationLease,
                partition);

        Assert.True(evidence.IsLegacyCertification);
        Assert.Equal(run.Id, evidence.SourceReceipt.RunId);
        Assert.Equal(stage.Id, evidence.SourceReceipt.StageId);
        Assert.Equal(
            partition.InputFingerprint,
            evidence.SourceReceipt.InputFingerprint);
        Assert.Equal(expectedOutputFingerprint, evidence.OutputFingerprint);
        PreparedTicketPartitionCertificationRecord certification =
            Assert.IsType<PreparedTicketPartitionCertificationRecord>(
                await database.Database.GetPartitionCertificationAsync(
                    refreshRunId,
                    partition.PartitionKey));
        Assert.Equal(run.Id, certification.SourceRunId);
        Assert.Equal(stage.Id, certification.SourceStageId);
        Assert.Equal(
            expectedOutputFingerprint,
            certification.OutputFingerprint);
    }

    [Fact]
    public async Task ReconciliationGroupingStage_EmptyReplacementIsDurableAndStagedOnly()
    {
        using TestDatabase database = CreateDatabase();
        await SeedPreparedTicketAsync(database, "FHIR-1");
        await SeedPreparedTicketAsync(database, "FHIR-2");
        await SeedPreparedTicketAsync(database, "FHIR-50");
        await database.Database.SaveGroupingAsync(SamplePayload());
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

        string hashA = new('a', 64);
        string hashB = new('b', 64);
        PreparedTicketPublicationReconciliationComparison comparison = new(
            PreparedTicketPublicationReconciliationContract.CurrentVersion,
            "source-run",
            "source-snapshot",
            hashA,
            "generation-1",
            DateTimeOffset.Parse("2026-09-17T10:00:00Z"),
            hashA,
            [
                new PreparedTicketPublicationReconciliationItemDecision(
                    "FHIR-1",
                    PreparedTicketPublicationReconciliationDispositionValues
                        .ReAuthor,
                    "revision-1",
                    "revision-2",
                    "baseline-receipt",
                    "baseline-item",
                    "baseline-run",
                    hashA,
                    hashA,
                    "fhir",
                    "revision-2"),
            ]);
        AuthoringRunRecord run =
            await database.Database.CreatePublicationReconciliationAsync(
                comparison);
        Assert.True(await store.TryAcquireMutationFenceAsync(
            "jira-fhir",
            run.Id));
        string partitionKey = PreparerDatabase.GetPartitionKey(
            WorkGroupClean,
            Specification,
            Type);
        PreparedTicketPublicationReconciliationGroupingImpact impact = new(
            partitionKey,
            ["FHIR-1"],
            hashA,
            hashA,
            hashA);
        await database.Database
            .SavePublicationReconciliationGroupingImpactAsync(
                run.Id,
                impact);
        AuthoringRunStageRecord stage = await store.EnsureRunStageAsync(
            run.Id,
            PreparerDatabase.PublicationReconciliationGroupingStageName,
            partitionKey,
            hashB);
        AuthoringRunStageLease lease =
            Assert.IsType<AuthoringRunStageLease>(
                await store.TryStartRunStageAsync(stage.Id));
        PreparedTicketGroupingStageContext context = new(
            run.Id,
            stage.Id,
            lease.LeaseId,
            hashB,
            partitionKey,
            hashB,
            ["FHIR-1"],
            []);
        PreparedTicketGroupingPayload replacement = new()
        {
            WorkGroupClean = WorkGroupClean,
            WorkGroupDisplay = WorkGroupDisplay,
            Specification = Specification,
            Type = Type,
            Topics = [],
        };
        PreparedTicketPublicationStagedGroupingReplacement staged = new(
            run.Id,
            partitionKey,
            System.Text.Json.JsonSerializer.Serialize(replacement),
            AuthoringResultHasher.HashNormalizedUtf8(string.Empty),
            PreparedTicketPublicationContract
                .ComputeGroupingOutputFingerprint(replacement),
            hashA,
            DateTimeOffset.Parse("2026-09-17T10:30:00Z"));

        AuthoringConflictException stale =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => database.Database
                    .SavePublicationReconciliationGroupingStageAsync(
                        context with { StageLeaseId = "stale-lease" },
                        staged));
        Assert.Equal(AuthoringConflictCode.StageLeaseLost, stale.Code);

        AuthoringRunStageReceipt receipt =
            await database.Database
                .SavePublicationReconciliationGroupingStageAsync(
                    context,
                    staged);

        Assert.Equal(run.Id, receipt.RunId);
        Assert.Equal(stage.Id, receipt.StageId);
        Assert.Equal(partitionKey, receipt.PartitionKey);
        Assert.Equal(hashB, receipt.InputFingerprint);
        Assert.Equal(0, receipt.TopicRows);
        Assert.Equal(0, receipt.TopicGroupRows);
        Assert.Equal(0, receipt.MemberRows);
        Assert.Equal(
            1,
            Count(
                database,
                "prepared_ticket_publication_staged_grouping"));
        Assert.Equal(
            1,
            Count(
                database,
                "prepared_ticket_publication_grouping_stage_receipts"));
        Assert.Equal(1, Count(database, "prepared_ticket_topics"));
        PreparedTicketGroupingPartition canonical =
            Assert.IsType<PreparedTicketGroupingPartition>(
                await database.Database.GetGroupingAsync(
                    WorkGroupClean,
                    Specification,
                    Type));
        Assert.Equal(
            "Observation value polymorphism",
            Assert.Single(canonical.Topics).ShortDescription);
        PreparedTicketPublicationReconciliationGroupingImpact completed =
            Assert.Single(
                await database.Database
                    .GetPublicationReconciliationGroupingImpactsAsync(
                        run.Id));
        Assert.True(completed.Complete);
        Assert.Equal(
            staged.CorpusFingerprint,
            completed.StagedCorpusFingerprint);
        Assert.Equal(
            staged.OutputFingerprint,
            completed.StagedOutputFingerprint);

        AuthoringRunStageLease reclaimed =
            Assert.IsType<AuthoringRunStageLease>(
                await store.TryStartRunStageAsync(
                    stage.Id,
                    orphanedAfter: TimeSpan.FromMinutes(10),
                    now: DateTimeOffset.UtcNow.AddHours(1)));
        AuthoringRunStageReceipt replayed =
            await database.Database
                .SavePublicationReconciliationGroupingStageAsync(
                    context with { StageLeaseId = reclaimed.LeaseId },
                    staged);
        Assert.Equal(receipt, replayed);
        await Assert.ThrowsAsync<AuthoringConflictException>(
            () => store.CompleteRunStageAsync(stage.Id, lease.LeaseId));
        await store.CompleteRunStageAsync(stage.Id, reclaimed.LeaseId);
        Assert.NotNull(
            await database.Database
                .GetPublicationReconciliationGroupingStageReceiptAsync(
                    run.Id,
                    stage.Id,
                    partitionKey,
                    hashB));
    }

    [Fact]
    public void ReconciliationGroupingWorker_ReceivesExactOverlayStageContext()
    {
        string overlayFingerprint = new('a', 64);
        PreparedTicketPublicationGroupingWorkItem workItem = new(
            "run-1",
            PreparerDatabase.GetPartitionKey(
                WorkGroupClean,
                Specification,
                Type),
            WorkGroupClean,
            WorkGroupDisplay,
            Specification,
            Type,
            ["FHIR-2"],
            ["FHIR-1", "FHIR-2"],
            overlayFingerprint);
        AuthoringRunStageLease lease = new(
            "stage-1",
            "lease-1",
            1);
        PreviewPreparedTicketGroupingDispatcher dispatcher = new(
            Options.Create(new PreparerServiceOptions()),
            NullLogger<PreviewPreparedTicketGroupingDispatcher>.Instance);

        System.Diagnostics.ProcessStartInfo startInfo =
            dispatcher.CreateStartInfo(workItem, lease);

        Assert.Equal(
            "1",
            startInfo.Environment[
                "FHIR_AUGURY_GROUPING_RECONCILIATION"]);
        Assert.Equal(
            workItem.RunId,
            startInfo.Environment["FHIR_AUGURY_GROUPING_RUN_ID"]);
        Assert.Equal(
            lease.StageId,
            startInfo.Environment["FHIR_AUGURY_GROUPING_STAGE_ID"]);
        Assert.Equal(
            lease.LeaseId,
            startInfo.Environment[
                "FHIR_AUGURY_GROUPING_STAGE_LEASE_ID"]);
        Assert.Equal(
            workItem.PartitionKey,
            startInfo.Environment[
                "FHIR_AUGURY_GROUPING_PARTITION_KEY"]);
        Assert.Equal(
            overlayFingerprint,
            startInfo.Environment[
                "FHIR_AUGURY_GROUPING_INPUT_FINGERPRINT"]);
        Assert.Equal(
            overlayFingerprint,
            startInfo.Environment[
                "FHIR_AUGURY_GROUPING_OVERLAY_CORPUS_FINGERPRINT"]);
        Assert.Equal(
            "[\"FHIR-2\"]",
            startInfo.Environment[
                "FHIR_AUGURY_GROUPING_REVISED_TICKET_KEYS_JSON"]);
        Assert.Equal(
            "[\"FHIR-1\",\"FHIR-2\"]",
            startInfo.Environment[
                "FHIR_AUGURY_GROUPING_TICKET_KEYS_JSON"]);
    }

    [Fact]
    public void ReconciliationGroupingImpactFingerprint_IsOrderIndependentAndComplete()
    {
        string hashA = new('a', 64);
        string hashB = new('b', 64);
        PreparedTicketPublicationReconciliationGroupingImpact first = new(
            "Orders\u001fFHIR\u001fChange Request",
            ["FHIR-2", "FHIR-1"],
            hashA,
            hashA,
            hashA,
            hashB,
            hashB,
            hashB,
            Complete: true);
        PreparedTicketPublicationReconciliationGroupingImpact second = new(
            "Orders\u001fFHIR\u001fComment",
            ["FHIR-3"],
            hashA,
            hashA,
            hashA,
            hashB,
            hashB,
            hashB,
            Complete: true);

        Assert.Equal(
            PreparedTicketPublicationContract
                .ComputeGroupingImpactFingerprint([first, second]),
            PreparedTicketPublicationContract
                .ComputeGroupingImpactFingerprint([second, first]));
        Assert.Throws<ArgumentException>(() =>
            PreparedTicketPublicationContract
                .ComputeGroupingImpactFingerprint(
                    [first with { Complete = false }]));
    }

    [Fact]
    public void ReconciliationGroupingFingerprint_BindsCompleteOutputSeparatelyFromImpactAudit()
    {
        string hashA = new('a', 64);
        string hashB = new('b', 64);
        string hashC = new('c', 64);
        PreparedTicketPublicationGroupingPartition unaffected = new(
            "Orders\u001fCDA\u001fChange Request",
            hashA);
        PreparedTicketPublicationReconciliationGroupingImpact replacement = new(
            "Orders\u001fFHIR\u001fChange Request",
            ["FHIR-2", "FHIR-1"],
            hashA,
            hashA,
            hashA,
            hashB,
            hashB,
            hashB,
            Complete: true);
        PreparedTicketPublicationReconciliationGroupingImpact second = replacement with
        {
            PartitionKey = "Orders\u001fFHIR\u001fComment",
            RevisedTicketKeys = ["FHIR-3"],
            StagedOutputFingerprint = hashC,
        };
        string full = PreparedTicketPublicationContract
            .ComputeReconciliationGroupingFingerprint(
                [unaffected],
                [replacement, second]);
        string audit = PreparedTicketPublicationContract
            .ComputeGroupingImpactFingerprint([replacement, second]);

        Assert.Equal(
            PreparedTicketPublicationContract.ComputeGroupingFingerprint(
                new PreparedTicketPublicationGroupingPartition[]
                {
                    unaffected,
                    new(replacement.PartitionKey, hashB),
                    new(second.PartitionKey, hashC),
                }),
            full);
        Assert.Equal(
            full,
            PreparedTicketPublicationContract
                .ComputeReconciliationGroupingFingerprint(
                    [unaffected],
                    [second, replacement]));
        Assert.NotEqual(audit, full);
        Assert.NotEqual(
            full,
            PreparedTicketPublicationContract
                .ComputeReconciliationGroupingFingerprint(
                    [unaffected with { OutputFingerprint = hashB }],
                    [replacement, second]));
        Assert.NotEqual(
            full,
            PreparedTicketPublicationContract
                .ComputeReconciliationGroupingFingerprint(
                    [unaffected],
                    [replacement with { StagedOutputFingerprint = hashC }, second]));

        PreparedTicketPublicationReconciliationGroupingImpact differentAudit =
            replacement with { BaselineOutputFingerprint = hashC };
        Assert.Equal(
            full,
            PreparedTicketPublicationContract
                .ComputeReconciliationGroupingFingerprint(
                    [unaffected],
                    [differentAudit, second]));
        Assert.NotEqual(
            audit,
            PreparedTicketPublicationContract
                .ComputeGroupingImpactFingerprint([differentAudit, second]));
        Assert.Throws<ArgumentException>(() =>
            PreparedTicketPublicationContract
                .ComputeReconciliationGroupingFingerprint(
                    [unaffected],
                    [replacement with { Complete = false }]));
        Assert.Throws<ArgumentException>(() =>
            PreparedTicketPublicationContract
                .ComputeReconciliationGroupingFingerprint(
                    [unaffected],
                    [replacement with { StagedOutputFingerprint = null }]));
        Assert.Throws<ArgumentException>(() =>
            PreparedTicketPublicationContract
                .ComputeReconciliationGroupingFingerprint(
                    [unaffected],
                    [replacement with { StagedCorpusFingerprint = null }]));
        Assert.Throws<ArgumentException>(() =>
            PreparedTicketPublicationContract
                .ComputeReconciliationGroupingFingerprint(
                    [unaffected],
                    [replacement with { StagedProtectedRowsFingerprint = null }]));
        Assert.Throws<ArgumentException>(() =>
            PreparedTicketPublicationContract
                .ComputeReconciliationGroupingFingerprint(
                    [unaffected],
                    [replacement, replacement]));
        Assert.Throws<ArgumentException>(() =>
            PreparedTicketPublicationContract
                .ComputeReconciliationGroupingFingerprint(
                    [new(replacement.PartitionKey, hashA)],
                    [replacement]));
    }

    [Fact]
    public void ReconciliationGroupingFingerprint_RemovedPartitionRemainsOnlyInImpactAudit()
    {
        string hash = new('a', 64);
        PreparedTicketPublicationGroupingPartition unaffected = new(
            "Orders\u001fCDA\u001fChange Request",
            hash);
        PreparedTicketPublicationReconciliationGroupingImpact removed = new(
            "Orders\u001fFHIR\u001fChange Request",
            ["FHIR-1"],
            hash,
            hash,
            hash,
            AuthoringResultHasher.HashNormalizedUtf8(string.Empty),
            hash,
            hash,
            Complete: true);

        Assert.Equal(
            PreparedTicketPublicationContract.ComputeGroupingFingerprint(
                [unaffected]),
            PreparedTicketPublicationContract
                .ComputeReconciliationGroupingFingerprint(
                    [unaffected],
                    [removed]));
        Assert.NotEqual(
            PreparedTicketPublicationContract.ComputeGroupingImpactFingerprint([]),
            PreparedTicketPublicationContract.ComputeGroupingImpactFingerprint([removed]));
    }

    private static PreparedTicketGroupingPayload SamplePayload() => new()
    {
        WorkGroupClean = WorkGroupClean,
        WorkGroupDisplay = WorkGroupDisplay,
        Specification = Specification,
        Type = Type,
        Topics =
        [
            new PreparedTicketTopicPayload
            {
                ShortDescription = "Observation value polymorphism",
                LongerDescription = "Covers ticket fan-out around Observation.value.",
                RenderOrderHint = 0,
                LinkedTicketGroups =
                [
                    new PreparedTicketTopicGroupPayload
                    {
                        FirstTicketKey = "FHIR-1",
                        Rationale = "Both edit `Observation.value[x]`.",
                        Members =
                        [
                            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-1", Order = 0 },
                            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-2", Order = 1 },
                        ],
                    },
                ],
                RemainingTicketKeys = ["FHIR-50"],
            },
        ],
    };

    private static async Task SeedPreparedTicketAsync(TestDatabase database, string key)
    {
        await database.Database.SavePreparedTicketAsync(CreatePreparedTicketPayload(key));
    }

    private static PreparedTicketPayload CreatePreparedTicketPayload(string key)
        => new()
        {
            Key = key,
            RequestSummary = "summary",
            CommentSummary = "comments",
            LinkedTicketSummary = "linked",
            RelatedTicketSummary = "related",
            RelatedZulipSummary = "zulip",
            RelatedGitHubSummary = "github",
            ExistingProposed = "existing",
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
            Repos = [new PreparedTicketRepoPayload { Repo = "HL7/fhir", RepoCategory = "FHIR Core", Justification = "r" }],
        };

    private static async Task SeedHydrationSelfAsync(TestDatabase database, string ticketKey, string workGroup, string type, string specification)
    {
        DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
        await using SqliteConnection connection = database.Database.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO prepared_jira_hydration
            (Id, TicketKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain, WorkGroup, WorkGroupClean, Specification, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason)
            VALUES
            (@id, @ticket, @ticket, @title, @status, @type, @priority, NULL, NULL, @workGroup, @workGroupClean, @specification, @updatedAt, @url, @hydratedAt, 'resolved', NULL)
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@ticket", ticketKey);
        command.Parameters.AddWithValue("@title", "title");
        command.Parameters.AddWithValue("@status", "Open");
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@priority", "Major");
        command.Parameters.AddWithValue("@workGroup", workGroup);
        command.Parameters.AddWithValue("@workGroupClean", FhirAugury.Common.WorkGroups.Hl7WorkGroupNameCleaner.Clean(workGroup));
        command.Parameters.AddWithValue("@specification", specification);
        command.Parameters.AddWithValue("@updatedAt", hydratedAt.ToString("O"));
        command.Parameters.AddWithValue("@url", $"https://jira.example.com/{ticketKey}");
        command.Parameters.AddWithValue("@hydratedAt", hydratedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static int Count(TestDatabase database, string table)
    {
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int CountWhere(TestDatabase database, string table, string whereClause)
    {
        using SqliteConnection connection = database.Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {whereClause}";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static TestDatabase CreateDatabase()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "preparer-grouping-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preparer.db");
        PreparerDatabase database = new(path, NullLogger<PreparerDatabase>.Instance);
        database.Initialize();
        return new TestDatabase(directory, database);
    }

    internal sealed class TestDatabase(string directory, PreparerDatabase database) : IDisposable
    {
        public PreparerDatabase Database { get; } = database;

        public void Dispose()
        {
            Database.Dispose();
            TestFileCleanup.SafeDeleteDirectory(directory);
        }
    }
}
