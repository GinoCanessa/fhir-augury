using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Api;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannedTicketGroupingPersistenceTests
{
    [Fact]
    public async Task RunPartitionFingerprintIncludesCurrentMembershipAcrossRunsAndRejectsStaleLease()
    {
        using Fixture fixture = new();
        await fixture.CompleteTicketRunAsync("FHIR-1", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringReceiptAcceptance receipt) =
            await fixture.AuthorTicketAsync("FHIR-2", new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
        await fixture.SeedHydrationAsync("FHIR-2");
        await fixture.AuthoringStore.MarkItemCompleteAsync(item.Id, receipt.Receipt.ReceiptId);

        PlannedTicketRunPartition partition =
            Assert.Single(await fixture.Database.GetRunPartitionsAsync(run.Id));
        Assert.Equal(["FHIR-1", "FHIR-2"], partition.TicketKeys);
        string expected = AuthoringResultHasher.HashNormalizedUtf8(
            string.Join(
                "\n",
                new[]
                {
                    $"FHIR-1:{fixture.ReceiptFor("FHIR-1")}",
                    $"FHIR-2:{receipt.Receipt.ReceiptId}",
                }));
        Assert.Equal(expected, partition.InputFingerprint);

        AuthoringRunStageRecord stage = await fixture.AuthoringStore.EnsureRunStageAsync(
            run.Id,
            "grouping",
            partition.PartitionKey,
            partition.InputFingerprint);
        AuthoringRunStageLease lease = Assert.IsType<AuthoringRunStageLease>(
            await fixture.AuthoringStore.TryStartRunStageAsync(stage.Id));
        PlannedTicketTopicGroupingPayload empty = new()
        {
            WorkGroupClean = partition.WorkGroupClean,
            WorkGroupDisplay = partition.WorkGroupDisplay,
            Specification = partition.Specification,
            Type = partition.Type,
            Topics = [],
        };

        await fixture.Database.SaveTopicGroupingForRunAsync(
            empty,
            run.Id,
            stage.Id,
            lease.LeaseId,
            partition.InputFingerprint);
        await Assert.ThrowsAsync<AuthoringConflictException>(
            () => fixture.Database.SaveTopicGroupingForRunAsync(
                empty,
                run.Id,
                stage.Id,
                "stale-lease",
                partition.InputFingerprint));
        Assert.Equal(
            1,
            fixture.Scalar<int>("SELECT COUNT(*) FROM planned_ticket_partition_receipts"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private readonly JiraProcessingSourceTicketStore _sourceStore;
        private readonly JiraAuthoringRunCoordinator _coordinator;
        private readonly Dictionary<string, string> _receipts = new(StringComparer.Ordinal);

        public Fixture()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                $"fhir-augury-planner-grouping-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, "planner.db");
            Database = new PlannerDatabase(path, NullLogger<PlannerDatabase>.Instance);
            Database.Initialize();
            AuthoringStore = new AuthoringRunStore(Database);
            _sourceStore = new JiraProcessingSourceTicketStore(path);
            _coordinator = new JiraAuthoringRunCoordinator(
                AuthoringStore,
                _sourceStore,
                new JiraProcessingFilterResolver(),
                Options.Create(new JiraProcessingOptions
                {
                    AgentCliCommand = "agent {ticketKey}",
                    JiraSourceAddress = "http://source",
                    SourceTicketShape = "fhir",
                    TicketStatusesToProcess = ["Resolved - change required"],
                }));
            AuthoringStore.EnsureProcessorModeAsync(_coordinator.ProcessorKind).GetAwaiter().GetResult();
            AuthoringStore.TransitionProcessorModeAsync(
                _coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.Legacy,
                AuthoringStatusValues.ProcessorModes.CuttingOver).GetAwaiter().GetResult();
            AuthoringStore.TransitionProcessorModeAsync(
                _coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.CuttingOver,
                AuthoringStatusValues.ProcessorModes.RunBacked).GetAwaiter().GetResult();
        }

        public PlannerDatabase Database { get; }
        public AuthoringRunStore AuthoringStore { get; }

        public async Task CompleteTicketRunAsync(string key, DateTimeOffset revision)
        {
            (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringReceiptAcceptance receipt) =
                await AuthorTicketAsync(key, revision);
            await SeedHydrationAsync(key);
            await AuthoringStore.MarkItemCompleteAsync(item.Id, receipt.Receipt.ReceiptId);
            await AuthoringStore.MarkRunFinalizingAsync(run.Id);
            await AuthoringStore.CompleteRunAsync(run.Id, snapshotId: null);
        }

        public async Task<(AuthoringRunRecord Run, AuthoringRunItemRecord Item, AuthoringReceiptAcceptance Receipt)> AuthorTicketAsync(
            string key,
            DateTimeOffset revision)
        {
            JiraProcessingSourceTicketRecord source = await _sourceStore.UpsertAsync(
                new JiraIssueSummaryEntry
                {
                    Key = key,
                    ProjectKey = "FHIR",
                    Title = key,
                    Type = "Change Request",
                    Status = "Resolved - change required",
                    WorkGroup = "FHIR Infrastructure",
                    Specification = "FHIR",
                    UpdatedAt = revision,
                },
                "fhir",
                false,
                CancellationToken.None);
            JiraAuthoringRunCreation creation = await _coordinator.CreateOneItemRunAsync(source);
            AuthoringRunItemRecord item = Assert.Single(creation.Items);
            AuthoringOperationClaim claim =
                (await AuthoringStore.ClaimItemAsync(creation.Run.Id, item.Id))!;
            PlannedTicketPayload payload = new()
            {
                Key = key,
                ResolutionSummary = key,
            };
            string hash = PlannedTicketAuthoringDtos.ComputeContentHash(payload);
            AuthoringReceiptAcceptance receipt = await AuthoringStore.AcceptResultAsync(
                new AuthoringResultSubmission(
                    creation.Run.Id,
                    item.Id,
                    claim.OperationId,
                    item.ExpectedSourceRevision,
                    hash),
                claim.OperationToken,
                (connection, ct) => Database.SavePlannedTicketForAuthoringAsync(
                    connection,
                    payload,
                    hash,
                    creation.Run.Id,
                    item.Id,
                    claim.OperationId,
                    ct));
            _receipts[key] = receipt.Receipt.ReceiptId;
            return (creation.Run, item, receipt);
        }

        public Task SeedHydrationAsync(string key)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Database.SaveHydrationAsync(
                new HydrationBatch(
                    key,
                    new HydrationTicketRow(
                        key, null, null, null, "FHIR", null, null, null, null, null, 0,
                        null, now, "resolved", null),
                    [
                        new HydrationJiraRow(
                            key, key, key, "Resolved - change required", "Change Request",
                            null, null, null, "FHIR Infrastructure", "FHIR", now,
                            $"https://jira/{key}", now, "resolved", null),
                    ],
                    [],
                    [],
                    [],
                    []),
                CancellationToken.None);
        }

        public string ReceiptFor(string key) => _receipts[key];

        public T Scalar<T>(string sql)
        {
            using SqliteConnection connection = Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
