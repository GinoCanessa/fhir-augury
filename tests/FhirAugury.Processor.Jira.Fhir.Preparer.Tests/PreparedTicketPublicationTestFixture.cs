using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Common.Text;
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
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

// Baseline cleanup assertions observe process-wide temporary directories.
[CollectionDefinition(PreparedTicketPublicationTestCollection.Name, DisableParallelization = true)]
public sealed class PreparedTicketPublicationTestCollection
{
    public const string Name = "Prepared ticket publication snapshots";
}

internal static class PreparedTicketPublicationTestFixture
{
    internal sealed record SourceResult(
        AuthoringRunRecord Run,
        AuthoringSnapshotDescriptor Descriptor,
        IReadOnlyDictionary<string, string> ExpectedRevisions,
        IReadOnlyDictionary<string, string> ReceiptIds);

    internal sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private readonly JiraProcessingSourceTicketStore _sourceStore;
        private readonly JiraAuthoringRunCoordinator _coordinator;
        private readonly IOptions<PreparerServiceOptions> _options;
        private readonly bool _richGraph;

        public Fixture(bool richGraph = false, int schemaVersion = PreparedTicketSnapshotSchemaV3.Version)
        {
            _richGraph = richGraph;
            _directory = Path.Combine(
                Path.GetTempPath(),
                $"fhir-augury-publication-refresh-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            string databasePath =
                Path.Combine(_directory, "preparer.db");
            SnapshotDirectory =
                Path.Combine(_directory, "snapshots");
            Database = new PreparerDatabase(
                databasePath,
                NullLogger<PreparerDatabase>.Instance);
            Database.Initialize();
            Store = new AuthoringRunStore(Database);
            _sourceStore =
                new JiraProcessingSourceTicketStore(databasePath);
            IOptions<JiraProcessingOptions> jiraOptions =
                Options.Create(
                    new JiraProcessingOptions
                    {
                        AgentCliCommand = "agent {ticketKey}",
                        JiraSourceAddress = "http://source",
                        SourceTicketShape = "fhir",
                        TicketStatusesToProcess = ["Triaged"],
                    });
            _coordinator = new JiraAuthoringRunCoordinator(
                Store,
                _sourceStore,
                new JiraProcessingFilterResolver(),
                jiraOptions);
            _options = Options.Create(
                new PreparerServiceOptions
                {
                    SnapshotDirectory = SnapshotDirectory,
                    SnapshotSchemaVersion = schemaVersion,
                });
            ActivateAsync().GetAwaiter().GetResult();
        }

        public PreparerDatabase Database { get; }
        public AuthoringRunStore Store { get; }
        public string DirectoryPath => _directory;
        public string SnapshotDirectory { get; }
        public Action<string>? BeforeSourceSnapshot { get; set; }

        public PreparedTicketPublicationBaselineReader CreateBaselineReader(
            ILogger<PreparedTicketPublicationBaselineReader>? logger = null)
            => new(Store, _options, logger);

        public async Task<PreparedTicketPublicationProtectedInventory> ReadCurrentAsync(bool reverseEnumeration = false)
        {
            using SqliteConnection connection = Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = reverseEnumeration ? "PRAGMA reverse_unordered_selects = ON; BEGIN" : "BEGIN";
            command.ExecuteNonQuery();
            try
            {
                PreparedTicketPublicationProtectedInventory inventory =
                    await PreparedTicketPublicationProtectionReader.ReadCurrentAsync(connection);
                command.CommandText = "COMMIT";
                command.ExecuteNonQuery();
                return inventory;
            }
            catch
            {
                command.CommandText = "ROLLBACK";
                command.ExecuteNonQuery();
                throw;
            }
        }

        public int Execute(string sql, params (string Name, object? Value)[] parameters)
        {
            using SqliteConnection connection = Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach ((string name, object? value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
            return command.ExecuteNonQuery();
        }

        public T Scalar<T>(string sql)
        {
            using SqliteConnection connection = Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
        }

        public async Task<AuthoringRunRecord> CreateAdditionalCurrentOutputAsync(
            string specification,
            string[] ticketKeys,
            bool groupOutput = true,
            DateTimeOffset? sourceUpdatedAt = null)
        {
            AuthoringRunRecord run = await Store.CreateRunAsync(
                "jira-fhir",
                ticketKeys.Select(key => new AuthoringRunItemDefinition(
                    key, "fhir", sourceUpdatedAt?.ToString("O", CultureInfo.InvariantCulture) ?? $"additional-{key}")).ToArray(),
                databaseOnly: true,
                inputProvenance: [new("jira", new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero), 18)]);
            Assert.True(await Store.TryAcquireMutationFenceAsync("jira-fhir", run.Id));
            foreach (AuthoringRunItemRecord item in await Store.GetRunItemsAsync(run.Id))
            {
                AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
                    await Store.ClaimItemAsync(run.Id, item.Id));
                PreparedTicketPayload payload = CreateRichPayload(item.BusinessKey);
                string hash = PreparedTicketAuthoringDtos.ComputeContentHash(payload);
                AuthoringReceiptAcceptance acceptance = await Store.AcceptResultAsync(
                    new(run.Id, item.Id, claim.OperationId, item.ExpectedSourceRevision, hash),
                    claim.OperationToken,
                    (connection, ct) => Database.SavePreparedTicketForAuthoringAsync(
                        connection, payload, hash, run.Id, item.Id, claim.OperationId, ct));
                PreparedTicketHydrationBatch hydration = CreateRichHydration(item.BusinessKey);
                await Database.SaveHydrationAsync(hydration with
                {
                    Parent = hydration.Parent with { Specification = specification },
                    JiraRows = hydration.JiraRows.Select(row =>
                        row.JiraKey == item.BusinessKey ? row with { Specification = specification } : row).ToArray(),
                });
                await Store.MarkItemCompleteAsync(item.Id, acceptance.Receipt.ReceiptId);
            }
            await Store.MarkRunFinalizingAsync(run.Id);
            foreach (PreparedTicketRunPartition partition in groupOutput
                ? await Database.GetRunPartitionsAsync(run.Id) : [])
            {
                AuthoringRunStageRecord stage = await Store.EnsureRunStageAsync(
                    run.Id, "grouping", partition.PartitionKey, partition.InputFingerprint);
                AuthoringRunStageLease lease = Assert.IsType<AuthoringRunStageLease>(
                    await Store.TryStartRunStageAsync(stage.Id));
                await new RichGroupingDispatcher(Database).ReplaceGroupingAsync(run.Id, partition, lease, CancellationToken.None);
                await Store.CompleteRunStageAsync(stage.Id, lease.LeaseId);
            }
            await Store.CompleteRunAsync(run.Id, snapshotId: null);
            return (await Store.GetRunAsync(run.Id))!;
        }

        public async Task<SourceResult> CreateSourceRunAsync(
            params string[] ticketKeys)
            => await CreateSourceRunAtAsync(
                new DateTimeOffset(
                    2026,
                    9,
                    1,
                    0,
                    0,
                    0,
                    TimeSpan.Zero),
                ticketKeys);

        public async Task<SourceResult> CreateSourceRunAtAsync(
            DateTimeOffset? sourceUpdatedAt,
            params string[] ticketKeys)
        {
            List<JiraProcessingSourceTicketRecord> sources = [];
            foreach (string ticketKey in ticketKeys)
            {
                sources.Add(
                    await _sourceStore.UpsertAsync(
                        new JiraIssueSummaryEntry
                        {
                            Key = ticketKey,
                            ProjectKey = "FHIR",
                            Title = $"Title {ticketKey}",
                            Type = "Change Request",
                            Status = "Triaged",
                            WorkGroup = "FHIR-I",
                            Specification = "FHIR",
                            UpdatedAt = sourceUpdatedAt,
                        },
                        "fhir",
                        false,
                        new DateTimeOffset(
                            2001,
                            1,
                            1,
                            0,
                            0,
                            0,
                            TimeSpan.Zero),
                        1,
                        CancellationToken.None));
            }

            JiraAuthoringRunCreation creation =
                await _coordinator.CreateExplicitRunAsync(
                    sources,
                    databaseOnly: false);
            Assert.True(await Store.TryAcquireMutationFenceAsync(
                _coordinator.ProcessorKind,
                creation.Run.Id));
            Dictionary<string, string> revisions =
                new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> receiptIds =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (AuthoringRunItemRecord item in creation.Items)
            {
                revisions[item.BusinessKey] =
                    item.ExpectedSourceRevision;
                AuthoringOperationClaim claim =
                    Assert.IsType<AuthoringOperationClaim>(
                        await Store.ClaimItemAsync(
                            creation.Run.Id,
                            item.Id));
                PreparedTicketPayload payload =
                    _richGraph ? CreateRichPayload(item.BusinessKey) : CreatePayload(item.BusinessKey);
                string contentHash =
                    PreparedTicketAuthoringDtos.ComputeContentHash(
                        payload);
                AuthoringReceiptAcceptance acceptance =
                    await Store.AcceptResultAsync(
                        new AuthoringResultSubmission(
                            creation.Run.Id,
                            item.Id,
                            claim.OperationId,
                            item.ExpectedSourceRevision,
                            contentHash),
                        claim.OperationToken,
                        (connection, ct) =>
                            Database.SavePreparedTicketForAuthoringAsync(
                                connection,
                                payload,
                                contentHash,
                                creation.Run.Id,
                                item.Id,
                                claim.OperationId,
                                ct));
                receiptIds[item.BusinessKey] =
                    acceptance.Receipt.ReceiptId;
                await Database.SaveHydrationAsync(
                    _richGraph ? CreateRichHydration(item.BusinessKey) : CreateHydration(item.BusinessKey));
                await Store.MarkItemCompleteAsync(
                    item.Id,
                    acceptance.Receipt.ReceiptId);
            }

            BeforeSourceSnapshot?.Invoke(creation.Run.Id);
            AuthoringSnapshotDescriptor descriptor =
                Assert.IsType<AuthoringSnapshotDescriptor>(
                    await CreateOrdinaryPostProcessor()
                        .FinalizeRunAsync(creation.Run.Id));
            return new SourceResult(
                (await Store.GetRunAsync(creation.Run.Id))!,
                descriptor,
                revisions,
                receiptIds);
        }

        public PreparedTicketPublicationRefreshService
            CreateRefreshService(
                OrchestratorHydrationFetcher fetcher,
                IPreparedTicketPublicationRefreshInterruptionHook?
                    hook = null,
                AuthoringRunSchedulerWakeSignal? wakeSignal = null)
        {
            AuthoringRetryPolicy retryPolicy = new(
                Options.Create(new ProcessingServiceOptions()));
            return new PreparedTicketPublicationRefreshService(
                Database,
                Store,
                new AuthoringRunControlService(Store, retryPolicy),
                _coordinator,
                CreateBaselineReader(),
                CreateEnricher(fetcher),
                wakeSignal ?? new AuthoringRunSchedulerWakeSignal(),
                NullLogger<
                    PreparedTicketPublicationRefreshService>.Instance,
                hook);
        }

        public PreparedTicketPublicationEnricher CreateEnricher(OrchestratorHydrationFetcher fetcher)
            => new(fetcher, NullLogger<PreparedTicketPublicationEnricher>.Instance);

        public async Task<PreparedTicketPublicationRefreshResult> CreateLegacyRefreshRunAsync(string sourceRunId)
        {
            AuthoringRunRecord run = await Store.CreateMaintenanceRunAsync(
                _coordinator.ProcessorKind,
                PreparerDatabase.GetPublicationRefreshMaintenanceItemsAsync,
                AuthoringRunPurposeValues.PublicationRefresh,
                databaseOnly: false,
                sourceRunId);
            Assert.Null(run.RequestJson);
            AuthoringRunControlStatus status = await new AuthoringRunControlService(
                Store, new AuthoringRetryPolicy(Options.Create(new ProcessingServiceOptions())))
                .GetStatusAsync(_coordinator.ProcessorKind, run.Id);
            return new(status.Run, status.Items);
        }

        public PreparedTicketRunPostProcessor CreatePostProcessor(
            PreparedTicketPublicationRefreshService service,
            IPreparedTicketGroupingDispatcher? dispatcher = null,
            int? snapshotSchemaVersion = null)
            => CreatePostProcessorCore(
                dispatcher ?? new RejectingGroupingDispatcher(),
                service,
                snapshotSchemaVersion);

        public void MakeGroupingReceiptsLegacy(string runId)
        {
            using SqliteConnection connection =
                Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE prepared_ticket_partition_receipts
                SET OutputFingerprint = NULL
                WHERE RunId = @runId
                """;
            command.Parameters.AddWithValue("@runId", runId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public int CountLive(string table)
        {
            using SqliteConnection connection =
                Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        public void SimulateReadySnapshotBeforeRunCompletion(
            string runId)
        {
            using SqliteConnection connection =
                Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE authoring_runs
                SET Status = 'finalizing',
                    SnapshotId = NULL,
                    CompletedAt = NULL
                WHERE Id = @runId;
                INSERT INTO authoring_mutation_fences(
                    ProcessorKind, RunId, LeaseId, AcquiredAt)
                VALUES(
                    'jira-fhir', @runId, @leaseId, @acquiredAt);
                """;
            command.Parameters.AddWithValue("@runId", runId);
            command.Parameters.AddWithValue(
                "@leaseId",
                Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue(
                "@acquiredAt",
                DateTimeOffset.UtcNow.ToString("O"));
            Assert.Equal(2, command.ExecuteNonQuery());
        }

        public string ReadAuthoredAndGroupingState()
            => DumpTables(
                "prepared_tickets",
                "prepared_ticket_repos",
                "prepared_ticket_related_jira",
                "prepared_ticket_related_zulip",
                "prepared_ticket_related_github",
                "prepared_ticket_topics",
                "prepared_ticket_topic_groups",
                "prepared_ticket_topic_members",
                "prepared_ticket_partition_receipts");

        public string ReadPublicationMetadataState()
            => DumpTables(
                "prepared_ticket_hydration",
                "prepared_jira_hydration",
                "prepared_zulip_hydration",
                "prepared_ticket_in_person_requesters",
                "authoring_run_input_provenance");

        private PreparedTicketRunPostProcessor
            CreateOrdinaryPostProcessor()
            => CreatePostProcessorCore(
                _richGraph ? new RichGroupingDispatcher(Database) : new SavingGroupingDispatcher(Database),
                publicationRefreshService: null);

        private PreparedTicketRunPostProcessor CreatePostProcessorCore(
            IPreparedTicketGroupingDispatcher dispatcher,
            PreparedTicketPublicationRefreshService?
                publicationRefreshService,
            int? snapshotSchemaVersion = null)
        {
            IOptions<PreparerServiceOptions> options = snapshotSchemaVersion is null
                ? _options
                : Options.Create(new PreparerServiceOptions
                {
                    SnapshotDirectory = SnapshotDirectory,
                    SnapshotSchemaVersion = snapshotSchemaVersion.Value,
                });
            HttpClient workGroupClient =
                new(new WorkGroupHandler())
                {
                    BaseAddress = new Uri("http://localhost/"),
                };
            SqliteReviewSnapshotReconciler reconciler = new(Store);
            return new PreparedTicketRunPostProcessor(
                Database,
                Store,
                new AuthoringRunFinalizer(Store),
                reconciler,
                _coordinator,
                new OrchestratorWorkGroupCatalogFetcher(
                    workGroupClient),
                dispatcher,
                options,
                publicationRefreshService,
                new PreparedTicketSnapshotMaterializer(
                    Database,
                    Store,
                    reconciler,
                    options));
        }

        private async Task ActivateAsync()
        {
            await Store.EnsureProcessorModeAsync(
                _coordinator.ProcessorKind);
            await Store.TransitionProcessorModeAsync(
                _coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.Legacy,
                AuthoringStatusValues.ProcessorModes.CuttingOver);
            await Store.TransitionProcessorModeAsync(
                _coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.CuttingOver,
                AuthoringStatusValues.ProcessorModes.RunBacked);
        }

        public string DumpTables(params string[] tables)
        {
            using SqliteConnection connection =
                Database.OpenConnection();
            List<string> results = [];
            foreach (string table in tables)
            {
                using SqliteCommand command =
                    connection.CreateCommand();
                command.CommandText =
                    $"SELECT * FROM {table} ORDER BY RowId";
                using SqliteDataReader reader =
                    command.ExecuteReader();
                List<string> rows = [];
                while (reader.Read())
                {
                    rows.Add(
                        string.Join(
                            "\u001f",
                            Enumerable.Range(0, reader.FieldCount)
                                .Select(index =>
                                    reader.IsDBNull(index)
                                        ? "<null>"
                                        : Convert.ToString(
                                            reader.GetValue(index),
                                            System.Globalization
                                                .CultureInfo
                                                .InvariantCulture) ??
                                          string.Empty)));
                }
                results.Add($"{table}\n{string.Join("\n", rows)}");
            }
            return string.Join("\n--\n", results);
        }

        public void Dispose()
        {
            Database.Dispose();
            SqliteConnection.ClearAllPools();
            TestFileCleanup.SafeDeleteDirectory(_directory);
        }
    }

    internal sealed class PublicationHttpHandler : HttpMessageHandler
    {
        public const string MessageUrl =
            "https://chat.fhir.org/#narrow/stream/implementers/topic/Publication/near/12345";
        public static readonly DateTimeOffset SourceRefreshedAt =
            new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

        private readonly Fixture _fixture;
        private readonly AuthoringRunControlService _control;

        public PublicationHttpHandler(Fixture fixture, IReadOnlyDictionary<string, DateTimeOffset> jiraUpdates)
        {
            _fixture = fixture;
            _control = new AuthoringRunControlService(
                fixture.Store, new AuthoringRetryPolicy(Options.Create(new ProcessingServiceOptions())));
            AddJiraItems(jiraUpdates);
        }

        public Dictionary<string, ItemResponse> JiraItems { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> JiraRequests { get; } = [];
        public List<string> ZulipRequests { get; } = [];
        public List<string> Requests { get; } = [];

        public HttpClient CreateClient()
            => new(this) { BaseAddress = new Uri("http://publication-fixture.invalid/") };

        public void AddJiraItems(IReadOnlyDictionary<string, DateTimeOffset> jiraUpdates)
        {
            foreach ((string key, DateTimeOffset updatedAt) in jiraUpdates)
            {
                JiraItems.Add(key, new ItemResponse
                {
                    Source = "jira",
                    Id = key,
                    Title = $"Title {key}",
                    UpdatedAt = updatedAt.ToUniversalTime(),
                    People = new ItemPeopleResponse(
                        key == "FHIR-806" ? null : $"Reporter {key}",
                        key is "FHIR-29212" or "FHIR-806" ? null : $"Assignee {key}",
                        key is "FHIR-29212" or "FHIR-806" ? [] : [$"Requester {key}"])
                    {
                        PublicDisplayNamePolicyVersion = PublicDisplayNamePolicy.CurrentVersion,
                    },
                    Provenance = new SourceReadProvenance
                    {
                        Source = "jira",
                        ContentRevision = 901,
                        IsStable = true,
                        ProjectLastSuccessfulRefreshAt = new Dictionary<string, DateTimeOffset?>
                        {
                            [key[..key.IndexOf('-')]] = SourceRefreshedAt,
                        },
                    },
                });
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri uri = Assert.IsType<Uri>(request.RequestUri);
            Assert.Equal("publication-fixture.invalid", uri.Host);
            Assert.Equal(HttpMethod.Get, request.Method);
            Requests.Add(uri.PathAndQuery);
            const string jiraPrefix = "/api/v1/jira/items/";
            const string runPrefix = "/api/v1/processing-services/Preparer/authoring/runs/";
            if (uri.AbsolutePath.StartsWith(jiraPrefix, StringComparison.Ordinal))
            {
                string key = Uri.UnescapeDataString(uri.AbsolutePath[jiraPrefix.Length..]);
                JiraRequests.Add(key);
                return Json(JiraItems[key]);
            }
            if (uri.AbsolutePath == "/api/v1/zulip/references/resolve")
            {
                Assert.StartsWith("?reference=", uri.Query, StringComparison.Ordinal);
                string reference = Uri.UnescapeDataString(uri.Query["?reference=".Length..]);
                ZulipRequests.Add(reference);
                return reference switch
                {
                    "12345" => Json(new ZulipReferenceResolutionResponse
                    {
                        Reference = reference,
                        Outcome = ZulipReferenceLookupOutcome.Resolved,
                        Kind = ZulipReferenceKind.Message,
                        MessageId = 12345,
                        StreamId = 17,
                        StreamName = "implementers",
                        Topic = "Publication",
                        MessageCount = 4,
                        FirstMessageAt = null,
                        LastMessageAt = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero),
                        FirstMessageExcerpt = "Indexed discussion for the accepted proposal.",
                        Url = MessageUrl,
                        Diagnostics = [ZulipReferenceDiagnosticCode.InvalidTimestamp],
                    }),
                    "stream::topic" => Json(new ZulipReferenceResolutionResponse
                    {
                        Reference = reference,
                        Outcome = ZulipReferenceLookupOutcome.NotFound,
                    }, HttpStatusCode.NotFound),
                    _ => throw new InvalidOperationException($"Unexpected synthetic Zulip reference '{reference}'."),
                };
            }
            if (uri.AbsolutePath.StartsWith(runPrefix, StringComparison.Ordinal))
            {
                string[] coordinate = uri.AbsolutePath[runPrefix.Length..].Split('/');
                string runId = coordinate[0];
                if (coordinate.Length == 1)
                {
                    AuthoringRunControlStatus status = await _control.GetStatusAsync("jira-fhir", runId, cancellationToken);
                    return Json(new AuthoringRunResponse(status.Run, status.Items));
                }
                AuthoringRunRecord run = Assert.IsType<AuthoringRunRecord>(
                    await _fixture.Store.GetRunAsync(runId, cancellationToken));
                AuthoringSnapshotDescriptor descriptor = Assert.IsType<AuthoringSnapshotDescriptor>(
                    await _fixture.Store.GetSnapshotDescriptorAsync(
                        Assert.IsType<string>(run.SnapshotId), cancellationToken));
                if (coordinate is [_, "snapshot"])
                {
                    return Json(descriptor);
                }
                if (coordinate is [_, "snapshot", "bytes"])
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(File.OpenRead(
                            Path.Combine(_fixture.SnapshotDirectory, descriptor.FileName))),
                    };
                }
            }
            throw new InvalidOperationException($"Unexpected synthetic HTTP request '{request.Method} {uri.PathAndQuery}'.");
        }

        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = JsonContent.Create(value, options: JsonSerializerOptions.Web) };
    }

    internal sealed class MetadataFetcher
        : OrchestratorHydrationFetcher
    {
        private MetadataFetcher(
            IReadOnlyDictionary<string, string> expectedRevisions)
            : base(
                new HttpClient(),
                NullLogger.Instance)
        {
            ObservedRevisions =
                new Dictionary<string, string>(
                    expectedRevisions,
                    StringComparer.OrdinalIgnoreCase);
            ContentRevisions = expectedRevisions.Keys.ToDictionary(
                key => key,
                _ => 901L,
                StringComparer.OrdinalIgnoreCase);
            UpdatedAtByTicket = expectedRevisions.ToDictionary(
                pair => pair.Key,
                pair => DateTimeOffset.TryParse(
                    pair.Value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
                    ? (DateTimeOffset?)parsed : null,
                StringComparer.OrdinalIgnoreCase);
        }

        public Dictionary<string, string> ObservedRevisions { get; }
        public Dictionary<string, long> ContentRevisions { get; }
        public Dictionary<string, DateTimeOffset?> UpdatedAtByTicket { get; }
        public Dictionary<string, HydrationZulipRow> ZulipResults { get; } = new(StringComparer.Ordinal);
        public List<(string TicketKey, string Reference)> ZulipCalls { get; } = [];
        public Func<PublicationMetadataFetchResult, PublicationMetadataFetchResult>? TransformMetadata { get; set; }
        public Action<string>? OnJiraFetch { get; set; }
        public int CallCount { get; private set; }
        public int ZulipCallCount => ZulipCalls.Count;
        public bool ThrowWhenCalled { get; set; }
        public bool ThrowWhenZulipCalled { get; set; }

        public static MetadataFetcher For(
            IReadOnlyDictionary<string, string> expectedRevisions)
            => new(expectedRevisions);

        public override Task<PublicationMetadataFetchResult>
            FetchPublicationMetadataAsync(
                string ticketKey,
                DateTimeOffset hydratedAt,
                CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            if (ThrowWhenCalled)
            {
                throw new InvalidOperationException(
                    "Metadata fetch should have been skipped.");
            }
            OnJiraFetch?.Invoke(ticketKey);
            string revision = ObservedRevisions[ticketKey];
            PublicationMetadataFetchResult result = new(
                    ticketKey,
                    hydratedAt,
                    revision,
                    $"Reporter {ticketKey}",
                    $"Assignee {ticketKey}",
                    [$"Requester {ticketKey}"],
                    ticketKey[..ticketKey.IndexOf(
                        '-',
                        StringComparison.Ordinal)],
                    new DateTimeOffset(
                        2026,
                        9,
                        14,
                        12,
                        0,
                        0,
                        TimeSpan.Zero),
                    ContentRevisions[ticketKey],
                    true,
                    PublicDisplayNamePolicy.CurrentVersion,
                    Failure: null,
                    UpdatedAt: UpdatedAtByTicket[ticketKey]);
            return Task.FromResult(TransformMetadata?.Invoke(result) ?? result);
        }

        public override Task<HydrationZulipRow> FetchZulipAsync(
            string ticketKey, string threadId, DateTimeOffset hydratedAt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ZulipCalls.Add((ticketKey, threadId));
            if (ThrowWhenCalled || ThrowWhenZulipCalled)
            {
                throw new InvalidOperationException("Zulip fetch should have been skipped.");
            }
            HydrationZulipRow result = ZulipResults.TryGetValue(threadId, out HydrationZulipRow? configured)
                ? configured : ZulipResult(threadId);
            return Task.FromResult(result with { TicketKey = ticketKey, HydratedAt = hydratedAt });
        }

        public static HydrationZulipRow ZulipResult(
            string reference,
            ZulipReferenceLookupOutcome outcome = ZulipReferenceLookupOutcome.Resolved,
            bool optionalDetails = true)
        {
            bool resolved = outcome == ZulipReferenceLookupOutcome.Resolved;
            bool message = ZulipReferenceContract.TryGetMessageId(reference, out int messageId);
            int separator = reference.IndexOf(':');
            string stream = message ? "message stream" : reference[..separator];
            string topic = message ? "message topic" : reference[(separator + 1)..];
            string url = $"https://chat.fhir.org/#narrow/stream/{Uri.EscapeDataString(stream)}/topic/{Uri.EscapeDataString(topic)}";
            if (message)
            {
                url += $"/near/{messageId}";
            }
            DateTimeOffset observedAt = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);
            return new(
                string.Empty, reference, resolved ? 17 : null, resolved ? stream : null, resolved ? topic : null,
                resolved && optionalDetails ? 4 : null,
                resolved && optionalDetails ? observedAt.AddDays(-1) : null,
                resolved && optionalDetails ? observedAt : null,
                resolved && optionalDetails ? "Fresh indexed context" : null,
                resolved ? url : null, observedAt, resolved ? "resolved" : "unresolved",
                ZulipReferenceHydrationReason.Serialize(new()
                {
                    Backing = resolved ? ZulipReferenceBacking.TypedResolver : ZulipReferenceBacking.None,
                    LatestOutcome = outcome,
                    Diagnostics = [],
                }));
        }
    }

    private sealed class SavingGroupingDispatcher(
        PreparerDatabase database)
        : IPreparedTicketGroupingDispatcher
    {
        public async Task ReplaceGroupingAsync(
            string runId,
            PreparedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct)
        {
            await database.SaveGroupingForRunAsync(
                new PreparedTicketGroupingPayload
                {
                    WorkGroupClean = partition.WorkGroupClean,
                    WorkGroupDisplay = partition.WorkGroupDisplay,
                    Specification = partition.Specification,
                    Type = partition.Type,
                    Topics = [],
                },
                runId,
                lease.StageId,
                lease.LeaseId,
                partition.InputFingerprint,
                ct);
        }
    }

    private sealed class RichGroupingDispatcher(PreparerDatabase database) : IPreparedTicketGroupingDispatcher
    {
        public async Task ReplaceGroupingAsync(
            string runId,
            PreparedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct)
        {
            string[] keys = partition.TicketKeys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
            PreparedTicketGroupingPayload payload = new()
            {
                WorkGroupClean = partition.WorkGroupClean,
                WorkGroupDisplay = partition.WorkGroupDisplay,
                Specification = partition.Specification,
                Type = partition.Type,
                Topics = [],
            };
            if (keys.Length >= 2)
            {
                PreparedTicketTopicPayload topic = new()
                {
                    ShortDescription = "First topic",
                    LongerDescription = "First topic details\n\nKeep this text.",
                    RenderOrderHint = 2,
                    LinkedTicketGroups = [],
                };
                for (int index = 0; index + 1 < Math.Min(4, keys.Length); index += 2)
                {
                    topic.LinkedTicketGroups.Add(new PreparedTicketTopicGroupPayload
                    {
                        FirstTicketKey = keys[index],
                        Rationale = $"Group reason {index}",
                        Members =
                        [
                            new() { TicketKey = keys[index], Order = 0 },
                            new() { TicketKey = keys[index + 1], Order = 1 },
                        ],
                    });
                }
                payload.Topics.Add(topic);
            }
            if (keys.Length >= 6)
            {
                payload.Topics.Add(new PreparedTicketTopicPayload
                {
                    ShortDescription = "Second topic",
                    LongerDescription = "Second topic details",
                    RenderOrderHint = 1,
                    RemainingTicketKeys = keys.Skip(4).ToList(),
                });
            }
            await database.SaveGroupingForRunAsync(
                payload, runId, lease.StageId, lease.LeaseId, partition.InputFingerprint, ct);
        }
    }

    internal sealed class CountingGroupingDispatcher(
        PreparerDatabase database)
        : IPreparedTicketGroupingDispatcher
    {
        public int CallCount { get; private set; }

        public async Task ReplaceGroupingAsync(
            string runId,
            PreparedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct)
        {
            CallCount++;
            await new SavingGroupingDispatcher(database)
                .ReplaceGroupingAsync(
                    runId,
                    partition,
                    lease,
                    ct);
        }
    }

    private sealed class RejectingGroupingDispatcher
        : IPreparedTicketGroupingDispatcher
    {
        public Task ReplaceGroupingAsync(
            string runId,
            PreparedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "Publication refresh dispatched grouping work.");
    }

    private sealed class WorkGroupHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"workGroups":[{"code":"fhir-i","name":"FHIR Infrastructure","retired":false,"totalFileCount":1,"totalArtifactCount":1,"repos":[]}]}""",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                });
    }

    private static PreparedTicketPayload CreatePayload(string key)
        => new()
        {
            Key = key,
            RequestSummary = "Request",
            ProposalA = "A",
            ProposalAImpact =
                PreparedTicketImpactValues.NonSubstantive,
            ProposalB = "B",
            ProposalBImpact =
                PreparedTicketImpactValues.NonSubstantive,
            ProposalC = "C",
            Recommendation =
                PreparedTicketRecommendationValues.ProposalA,
            RecommendationJustification = "Because",
        };

    private static PreparedTicketPayload CreateRichPayload(string key)
    {
        PreparedTicketPayload payload = CreatePayload(key);
        payload.RequestSummary = $"Request {key}\n\nExact authored text: caf\u00e9.";
        payload.CommentSummary = $"Comments {key}";
        payload.LinkedTicketSummary = $"Linked {key}";
        payload.RelatedTicketSummary = $"Related {key}";
        payload.RelatedZulipSummary = $"Zulip {key}";
        payload.RelatedGitHubSummary = $"GitHub {key}";
        payload.ExistingProposed = $"Existing proposal {key}";
        payload.ProposalA = $"Proposal A {key}";
        payload.ProposalAJustification = $"Reason A {key}";
        payload.ProposalB = $"Proposal B {key}";
        payload.ProposalBJustification = $"Reason B {key}";
        payload.ProposalC = $"Proposal C {key}";
        payload.ProposalCJustification = $"Reason C {key}";
        payload.RecommendationJustification = $"Recommendation {key}";
        payload.Repos = [new() { Repo = "HL7/fhir", RepoCategory = "specification", Justification = $"Repo reason {key}" }];
        payload.RelatedJiraTickets =
        [
            new() { AssociatedTicketKey = "FHIR-99999", LinkType = "linked", Justification = $"Jira reason {key}" },
        ];
        payload.RelatedZulipThreads =
        [
            new() { ZulipThreadId = "stream::topic", Justification = $"Zulip reason {key}" },
            new() { ZulipThreadId = "12345", Justification = $"Missing hydration reason {key}" },
        ];
        payload.RelatedGitHubItems =
        [
            new() { GitHubItemId = "HL7/fhir#17", Justification = $"GitHub reason {key}" },
        ];
        return payload;
    }

    private static PreparedTicketHydrationBatch CreateRichHydration(string key)
    {
        PreparedTicketHydrationBatch hydration = CreateHydration(key);
        DateTimeOffset time = hydration.Parent.HydratedAt;
        PreparedJiraHydrationRow self = hydration.JiraRows[0] with
        {
            DescriptionHtml = $"<p>Self content {key}</p>",
            ResolutionDescriptionHtml = $"<p>Self resolution {key}</p>",
            CreatedAt = time.AddDays(-100),
            RelatedArtifactsRaw = "Patient",
            RelatedPagesRaw = "patient.html",
        };
        return hydration with
        {
            Parent = hydration.Parent with
            {
                RaisedInVersion = "R5",
                SelectedBallot = "2023-09",
                ChangeCategory = "Correction",
                Impact = "Non-substantive",
                Labels = "label-a, label-b",
                CommentCount = 8,
                DescriptionHtml = $"<p>Parent content {key}</p>",
                ResolutionDescriptionHtml = $"<p>Parent resolution {key}</p>",
                CreatedAt = time.AddDays(-100),
                RelatedArtifactsRaw = "Patient, Observation",
                RelatedPagesRaw = "patient.html, observation.html",
            },
            JiraRows = [self, self with
            {
                JiraKey = "FHIR-99999", Title = "Related Jira content", Type = "Bug",
                Reporter = "Related Reporter", Assignee = "Related Assignee",
            }],
            ZulipRows =
            [
                new(key, "stream::topic", 7, "stream", "topic", 3, time.AddHours(-1), time,
                    "An indexed excerpt", "https://chat.fhir.org/#narrow/stream/7/topic/topic",
                    time, "resolved", null),
                new(key, "unaccepted-reference", 8, "other", "unaccepted", 2, time, time,
                    "Must remain protected", "https://chat.fhir.org/#narrow/stream/8/topic/other",
                    time, "resolved", null),
            ],
            GitHubRows =
            [
                new(key, "HL7/fhir#17", "HL7", "fhir", 17, "source/patient.xml", "GitHub title", "open",
                    true, "github-label", time, "https://github.com/HL7/fhir/pull/17", time, "resolved", null),
            ],
            RepoRows =
            [
                new(key, "HL7/fhir", "Repository content", "FHIR Infrastructure", "FHIR", "Specification",
                    "https://github.com/HL7/fhir", time, "resolved", null),
            ],
            JiraXrefRows = [new(key, "FHIR-99999", "linked")],
        };
    }

    private static PreparedTicketHydrationBatch CreateHydration(
        string key)
    {
        DateTimeOffset hydratedAt =
            new(2002, 2, 2, 0, 0, 0, TimeSpan.Zero);
        return new PreparedTicketHydrationBatch(
            key,
            new PreparedTicketHydrationRow(
                key,
                "Major",
                "Persuasive",
                "Resolution",
                "FHIR",
                null,
                null,
                null,
                null,
                null,
                0,
                "Description",
                hydratedAt,
                "resolved",
                null,
                Reporter: "Legacy Reporter",
                Assignee: "Legacy Assignee",
                SourceProject: "FHIR",
                SourceLastSuccessfulRefreshAt:
                    hydratedAt.AddDays(-1),
                SourceContentRevision: 2,
                PublicDisplayNamePolicyVersion:
                    PublicDisplayNamePolicy.CurrentVersion),
            [
                new PreparedJiraHydrationRow(
                    key,
                    key,
                    $"Title {key}",
                    "Triaged",
                    "Change Request",
                    "Major",
                    "Persuasive",
                    "Resolution",
                    "FHIR Infrastructure",
                    "FHIR",
                    hydratedAt,
                    $"https://jira/{key}",
                    hydratedAt,
                    "resolved",
                    null,
                    Reporter: "Legacy Reporter",
                    Assignee: "Legacy Assignee",
                    PublicDisplayNamePolicyVersion:
                        PublicDisplayNamePolicy.CurrentVersion),
            ],
            [],
            [],
            [],
            [],
            [
                new PreparedTicketInPersonRequesterRow(
                    key,
                    "Legacy Requester",
                    PublicDisplayNamePolicy.CurrentVersion),
            ]);
    }
}
