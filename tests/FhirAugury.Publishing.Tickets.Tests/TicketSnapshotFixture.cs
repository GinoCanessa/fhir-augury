using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Common.Text;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Publishing.Tickets.Tests;

internal sealed class TicketSnapshotFixture
{
    public const string FirstJiraUpdatedAt = "2026-09-05T12:00:00.0000000+00:00";
    public const string SecondJiraUpdatedAt = "2026-09-06T15:30:00.0000000+00:00";

    public static IReadOnlyDictionary<string, string> DiscussionRuntimeBodies { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RequestPlain"] = "  Original request.\r\nKeep <literal> & | text.\n\n  ",
            ["ResolutionPlain"] = "\tOriginal resolution.\r\nDo not rewrite.  ",
            ["RequestSummary"] = "  Request FHIR-1002 and BALLOT-12.\r\nKeep this line.  ",
            ["CommentSummary"] = "\nComment FHIR-1004 and FHIR-1005suffix stays text.\t",
            ["LinkedTicketSummary"] = " Linked FHIR-1002 and FHIR-2002.\r\n ",
            ["RelatedTicketSummary"] = "\tRelated FHIR-2002.\nKeep | delimiters. ",
            ["RelatedZulipSummary"] = "  Authored discussion analysis for FHIR-1002.\r\nNever replace this with lookup diagnostics.\n  ",
            ["RelatedGitHubSummary"] = "\nGitHub context mentioning FHIR-2004.  ",
            ["ExistingProposed"] = "  Existing proposal.\r\nUnchanged.  ",
            ["ProposalA"] = "  Accept FHIR-1002 exactly.\r\nKeep | delimiters and <literal>.\n\n\tA ending  ",
            ["ProposalAJustification"] = "\tA because.\r\nKeep *authored* formatting.  ",
            ["ProposalAImpact"] = "  Non-substantive \r\n",
            ["ProposalB"] = "\n B modified proposal.\r\nDo not normalize.\t",
            ["ProposalBJustification"] = "  B because.\r\n  ",
            ["ProposalBImpact"] = "Non-substantive",
            ["ProposalC"] = "\tC rejection.\r\nPreserve exactly.  ",
            ["ProposalCJustification"] = " C because.\nNo invented impact.\r\n",
            ["Recommendation"] = "  A\r\n",
            ["RecommendationJustification"] = "\n Because this is the authored recommendation.\t ",
        };

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

    private TicketSnapshotFixture(
        string databasePath,
        string descriptorPath,
        AuthoringSnapshotDescriptor descriptor)
    {
        DatabasePath = databasePath;
        DescriptorPath = descriptorPath;
        Descriptor = descriptor;
    }

    public string DatabasePath { get; }
    public string DescriptorPath { get; }
    public AuthoringSnapshotDescriptor Descriptor { get; private set; }

    public async Task<VerifiedAuthoringSnapshotPair> CreateVerifiedPairAsync(
        string serviceName)
    {
        string pairDirectory = Path.Combine(
            Path.GetDirectoryName(DatabasePath) ?? Environment.CurrentDirectory,
            $"pair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pairDirectory);
        string databaseFileName = Path.GetFileName(DatabasePath);
        string descriptorFileName = Path.GetFileName(DescriptorPath);
        string pairDatabasePath = Path.Combine(
            pairDirectory,
            databaseFileName);
        string pairDescriptorPath = Path.Combine(
            pairDirectory,
            descriptorFileName);
        File.Copy(DatabasePath, pairDatabasePath);
        File.Copy(DescriptorPath, pairDescriptorPath);
        byte[] descriptorBytes =
            await File.ReadAllBytesAsync(pairDescriptorPath);
        AuthoringSnapshotPairManifest manifest = new(
            AuthoringSnapshotPairManifest.CurrentFormatVersion,
            serviceName,
            Descriptor.RunId,
            Descriptor.SnapshotId,
            descriptorFileName,
            databaseFileName,
            Descriptor.SizeBytes,
            Convert.ToHexString(SHA256.HashData(descriptorBytes))
                .ToLowerInvariant(),
            Descriptor.Sha256);
        await File.WriteAllTextAsync(
            Path.Combine(
                pairDirectory,
                AuthoringSnapshotPairManifest.ReadyFileName),
            JsonSerializer.Serialize(manifest, JsonOptions));
        return await new AuthoringSnapshotPairVerifier()
            .VerifyReadyPairAsync(
                serviceName,
                Descriptor.RunId,
                pairDirectory);
    }

    public async Task MoveSecondTicketToHistoricalLedgerAsync()
    {
        await using SqliteConnection connection = new(
            $"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            INSERT INTO authoring_runs(
                Id, ProcessorKind, AuthoringEpoch, Status, DatabaseOnly,
                TotalItems, CreatedAt, StartedAt, CompletedAt)
            VALUES(
                'historical-run', 'jira-fhir', 1, 'superseded', 0, 1,
                @createdAt, @createdAt, @createdAt);
            UPDATE authoring_run_items
            SET RunId = 'historical-run',
                Status = 'superseded'
            WHERE Id = 'item-2';
            UPDATE authoring_result_receipts
            SET RunId = 'historical-run'
            WHERE Id = 'receipt-2';
            UPDATE authoring_runs
            SET TotalItems = 1
            WHERE Id = @currentRunId;
            UPDATE authoring_snapshot_provenance
            SET ItemCount = 1,
                ReceiptCount = 2;
            """,
            ("@createdAt", Descriptor.CreatedAt.ToString("O")),
            ("@currentRunId", Descriptor.RunId));
        await connection.CloseAsync();
        await connection.DisposeAsync();

        Descriptor = await CreateDescriptorAsync(
            DatabasePath,
            Descriptor.RunId,
            Descriptor.SnapshotId,
            Descriptor.Sequence,
            Descriptor.SchemaVersion,
            Descriptor.TableCounts,
            Descriptor.CreatedAt,
            itemCount: 1,
            receiptCount: 2,
            publicationProof: Descriptor.PublicationProof);
        await WriteDescriptorAsync(DescriptorPath, Descriptor);
    }

    public async Task AttachValidPublicationRefreshProofAsync(
        DateTimeOffset sourceRefresh,
        long sourceContentRevision)
    {
        if (Descriptor.SchemaVersion != PreparedTicketSnapshotSchemaV3.Version)
        {
            throw new InvalidOperationException(
                "Publication-refresh proof fixtures require snapshot schema v3.");
        }

        PreparedTicketPublicationFingerprints fingerprints =
            await PreparedTicketPublicationFingerprintReader.ReadAsync(
                DatabasePath);
        string sourceRunId = Descriptor.RunId;
        string refreshRunId = $"refresh-{Guid.NewGuid():N}";
        string refreshItemPrefix = $"refresh-item-{Guid.NewGuid():N}-";
        await using (SqliteConnection connection = new(
            $"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(
                connection,
                """
                INSERT INTO authoring_runs(
                    Id, ProcessorKind, AuthoringEpoch, Status, DatabaseOnly,
                    TotalItems, CreatedAt, StartedAt, CompletedAt, SnapshotId)
                SELECT @refreshRunId, ProcessorKind, AuthoringEpoch,
                       'finalizing', 0,
                       (SELECT COUNT(*) FROM prepared_tickets),
                       @createdAt, @createdAt, NULL, @snapshotId
                FROM authoring_runs
                WHERE Id = @sourceRunId;

                INSERT INTO authoring_run_items(
                    Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision,
                    Status, AcceptedReceiptId, AttemptCount, CreatedAt,
                    StartedAt, CompletedAt)
                SELECT @refreshItemPrefix || item.Id, @refreshRunId,
                       item.BusinessKey,
                       'maintenance:' || @refreshRunId || ':' || item.ItemKind,
                       item.ExpectedSourceRevision, 'complete',
                       item.AcceptedReceiptId, 0, @createdAt, @createdAt,
                       @createdAt
                FROM authoring_run_items item
                INNER JOIN authoring_result_receipts receipt
                    ON receipt.Id = item.AcceptedReceiptId
                   AND receipt.RunId = item.RunId
                   AND receipt.RunItemId = item.Id
                INNER JOIN prepared_tickets ticket
                    ON ticket.Key = item.BusinessKey COLLATE NOCASE;

                UPDATE authoring_snapshot_provenance
                SET RunId = @refreshRunId,
                    ItemCount = (
                        SELECT COUNT(*)
                        FROM authoring_run_items
                        WHERE RunId = @refreshRunId
                    );
                """,
                ("@refreshRunId", refreshRunId),
                ("@sourceRunId", sourceRunId),
                ("@refreshItemPrefix", refreshItemPrefix),
                ("@createdAt", Descriptor.CreatedAt.ToString("O")),
                ("@snapshotId", Descriptor.SnapshotId));
            await connection.CloseAsync();
        }

        AuthoringSnapshotPublicationProof proof = new(
            PreparedTicketPublicationContract.CurrentVersion,
            PreparedTicketPublicationContract.PublicationRefreshPurpose,
            sourceRunId,
            PreparedTicketPublicationContract.JiraSourceName,
            sourceRefresh.ToUniversalTime(),
            sourceContentRevision,
            PublicDisplayNamePolicy.CurrentVersion,
            fingerprints.Corpus,
            fingerprints.Grouping,
            Descriptor.CreatedAt.ToUniversalTime());
        int itemCount;
        await using (SqliteConnection connection = new(
            $"Data Source={DatabasePath};Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM authoring_run_items WHERE RunId = @runId";
            command.Parameters.AddWithValue("@runId", refreshRunId);
            itemCount = Convert.ToInt32(await command.ExecuteScalarAsync());
        }
        Descriptor = await CreateDescriptorAsync(
            DatabasePath,
            refreshRunId,
            Descriptor.SnapshotId,
            Descriptor.Sequence,
            Descriptor.SchemaVersion,
            Descriptor.TableCounts,
            Descriptor.CreatedAt,
            itemCount,
            Descriptor.ReceiptCount,
            proof);
        await WriteDescriptorAsync(DescriptorPath, Descriptor);
    }

    public static async Task<TicketSnapshotFixture>
        CreatePromotedReconciliationAsync(
            string root,
            DateTimeOffset capturedAt,
            long stableJiraGeneration,
            bool canonicalEpochRecovery = false)
    {
        const string processorKind = "jira-fhir";
        const string ticketKey = "FHIR-1001";
        string[] ticketKeys = canonicalEpochRecovery
            ? [ticketKey, "FHIR-1002"]
            : [ticketKey];
        DateTimeOffset utcCapturedAt = capturedAt.ToUniversalTime();
        string fixtureDirectory = Path.Combine(
            root,
            $"preparer-reconciliation-{Guid.NewGuid():N}");
        string snapshotDirectory = Path.Combine(
            fixtureDirectory,
            "snapshots");
        Directory.CreateDirectory(fixtureDirectory);
        using PreparerDatabase database = new(
            Path.Combine(fixtureDirectory, "preparer.db"),
            NullLogger<PreparerDatabase>.Instance);
        database.Initialize();
        AuthoringRunStore store = new(database);
        await store.EnsureProcessorModeAsync(processorKind);
        await store.TransitionProcessorModeAsync(
            processorKind,
            AuthoringStatusValues.ProcessorModes.Legacy,
            AuthoringStatusValues.ProcessorModes.CuttingOver);
        await store.TransitionProcessorModeAsync(
            processorKind,
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            AuthoringStatusValues.ProcessorModes.RunBacked);

        AuthoringRunRecord sourceRun = await store.CreateRunAsync(
            processorKind,
            ticketKeys.Select(key => new AuthoringRunItemDefinition(key, "fhir", "rev-1")).ToArray(),
            now: utcCapturedAt.AddMinutes(-10),
            inputProvenance:
            [
                new(
                    PreparedTicketPublicationContract.JiraSourceName,
                    utcCapturedAt,
                    stableJiraGeneration),
            ]);
        if (!await store.TryAcquireMutationFenceAsync(
                processorKind,
                sourceRun.Id,
                utcCapturedAt.AddMinutes(-9)))
        {
            throw new InvalidOperationException(
                "The source fixture could not acquire its mutation fence.");
        }
        foreach (AuthoringRunItemRecord sourceItem in
                 await store.GetRunItemsAsync(sourceRun.Id))
        {
            AuthoringOperationClaim sourceClaim =
                await store.ClaimItemAsync(
                    sourceRun.Id,
                    sourceItem.Id,
                    utcCapturedAt.AddMinutes(-8))
                ?? throw new InvalidOperationException(
                    "The source fixture item could not be claimed.");
            PreparedTicketPayload payload =
                CreateReconciliationSourcePayload(sourceItem.BusinessKey, utcCapturedAt);
            string contentHash =
                PreparedTicketAuthoringDtos.ComputeContentHash(payload);
            AuthoringReceiptAcceptance sourceAcceptance =
                await store.AcceptResultAsync(
                    new(
                        sourceRun.Id,
                        sourceItem.Id,
                        sourceClaim.OperationId,
                        sourceItem.ExpectedSourceRevision,
                        contentHash),
                    sourceClaim.OperationToken,
                    (connection, ct) =>
                        database.SavePreparedTicketForAuthoringAsync(
                            connection,
                            payload,
                            contentHash,
                            sourceRun.Id,
                            sourceItem.Id,
                            sourceClaim.OperationId,
                            ct),
                    now: utcCapturedAt.AddMinutes(-7));
            await database.SaveHydrationAsync(
                CreateReconciliationSourceHydration(
                    sourceItem.BusinessKey,
                    utcCapturedAt,
                    stableJiraGeneration));
            await store.MarkItemCompleteAsync(
                sourceItem.Id,
                sourceAcceptance.Receipt.ReceiptId,
                now: utcCapturedAt.AddMinutes(-6));
        }
        await store.MarkRunFinalizingAsync(
            sourceRun.Id,
            now: utcCapturedAt.AddMinutes(-5));
        foreach (PreparedTicketRunPartition partition in
                 await database.GetRunPartitionsAsync(sourceRun.Id))
        {
            AuthoringRunStageRecord stage =
                await store.EnsureRunStageAsync(
                    sourceRun.Id,
                    "grouping",
                    partition.PartitionKey,
                    partition.InputFingerprint);
            AuthoringRunStageLease lease =
                await store.TryStartRunStageAsync(stage.Id)
                ?? throw new InvalidOperationException(
                    $"The source grouping stage '{stage.Id}' could not be claimed.");
            _ = await database.SaveGroupingForRunAsync(
                new PreparedTicketGroupingPayload
                {
                    WorkGroupClean = partition.WorkGroupClean,
                    WorkGroupDisplay = partition.WorkGroupDisplay,
                    Specification = partition.Specification,
                    Type = partition.Type,
                    Topics = canonicalEpochRecovery
                        ? [new()
                        {
                            ShortDescription = "Canonical recovery topic",
                            LongerDescription = "Complete retained grouping output",
                            RenderOrderHint = 1,
                            LinkedTicketGroups = [new()
                            {
                                FirstTicketKey = ticketKey,
                                Rationale = "Shared canonical context",
                                Members = ticketKeys.Select((key, index) =>
                                    new PreparedTicketTopicGroupMemberPayload
                                    {
                                        TicketKey = key,
                                        Order = index,
                                    }).ToList(),
                            }],
                            RemainingTicketKeys = [],
                        }]
                        : [],
                },
                sourceRun.Id,
                stage.Id,
                lease.LeaseId,
                partition.InputFingerprint);
            await store.CompleteRunStageAsync(stage.Id, lease.LeaseId);
        }

        IOptions<PreparerServiceOptions> options = Options.Create(
            new PreparerServiceOptions
            {
                SnapshotDirectory = snapshotDirectory,
                SnapshotSchemaVersion =
                    PreparedTicketSnapshotSchemaV3.Version,
            });
        SqliteReviewSnapshotReconciler snapshotReconciler = new(store);
        PreparedTicketSnapshotMaterializer materializer = new(
            database,
            store,
            snapshotReconciler,
            options);
        AuthoringRunRecord sourceFinalizing =
            await store.GetRunAsync(sourceRun.Id)
            ?? throw new InvalidOperationException(
                "The source fixture run was not persisted.");
        AuthoringSnapshotDescriptor sourceDescriptor =
            await materializer.MaterializeAsync(
                sourceFinalizing,
                PreparedTicketSnapshotSchemaV3.Catalog);
        await store.CompleteRunAsync(
            sourceRun.Id,
            sourceDescriptor.SnapshotId,
            now: utcCapturedAt.AddMinutes(-4));

        PreparedTicketPublicationProtectedInventory inventory;
        await using (SqliteConnection connection = database.OpenConnection())
        {
            inventory =
                await PreparedTicketPublicationProtectionReader
                    .ReadCurrentAsync(connection);
        }
        PreparedTicketPublicationReconciliationItemDecision[] decisions =
            inventory.Corpus.Select(coordinate =>
            {
                string graphHash = inventory.Rows.Single(row =>
                    row.Table == "prepared_ticket_authoring_state" &&
                    row.Scope == coordinate.TicketKey).Values
                    .Single(value => value.Column == "GraphHash").Value
                    ?? throw new InvalidOperationException(
                        "The source fixture has no authored graph fingerprint.");
                PreparedTicketPublicationProtectedGrouping partition =
                    inventory.Grouping.Single(partition =>
                        partition.Corpus.Any(item => item.TicketKey == coordinate.TicketKey));
                return new PreparedTicketPublicationReconciliationItemDecision(
                    coordinate.TicketKey,
                    PreparedTicketPublicationReconciliationDispositionValues.CarryForward,
                    coordinate.ExpectedSourceRevision,
                    coordinate.ExpectedSourceRevision,
                    coordinate.ReceiptId,
                    coordinate.RunItemId,
                    coordinate.ContributingRunId,
                    graphHash,
                    partition.Fingerprint.OutputFingerprint,
                    coordinate.ItemKind,
                    coordinate.ExpectedSourceRevision);
            }).ToArray();
        PreparedTicketPublicationReconciliationComparison comparison = new(
            PreparedTicketPublicationReconciliationContract.CurrentVersion,
            sourceRun.Id,
            sourceDescriptor.SnapshotId,
            sourceDescriptor.Sha256,
            stableJiraGeneration.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            utcCapturedAt,
            inventory.CorpusFingerprint,
            decisions);
        AuthoringRunRecord reconciliation =
            await database.CreatePublicationReconciliationAsync(
                comparison,
                now: utcCapturedAt.AddMinutes(-3));
        PreparedTicketGroupingDeltaDispatcher grouping = new(database);
        PreparedTicketPublicationGroupingDelta delta =
            await grouping.PrepareAsync(reconciliation.Id);
        if (delta.Impacts.Count != 0)
        {
            throw new InvalidOperationException(
                "The carry-forward reconciliation fixture unexpectedly requires grouping replacement.");
        }
        await store.MarkRunFinalizingAsync(
            reconciliation.Id,
            now: utcCapturedAt.AddMinutes(-2));
        PreparedTicketPublicationReconciliationProof proof =
            await grouping.CreateProofAsync(
                reconciliation.Id,
                capturedAt: utcCapturedAt);
        AuthoringRunRecord reconciliationFinalizing =
            await store.GetRunAsync(reconciliation.Id)
            ?? throw new InvalidOperationException(
                "The reconciliation fixture run was not persisted.");
        _ = await materializer.MaterializeReconciliationCandidateAsync(
            reconciliationFinalizing,
            proof);
        string finalPath = Path.Combine(
            snapshotDirectory,
            $"{processorKind}-{reconciliation.Id}.db");
        PreparerDatabase.PublicationReconciliationPromotion promotion =
            await database.PromotePublicationReconciliationAsync(
                reconciliation.Id,
                finalPath);
        PreparedTicketPublicationRecoveryService recovery = new(
            database,
            store,
            NullLogger<PreparedTicketPublicationRecoveryService>.Instance);
        AuthoringSnapshotDescriptor descriptor;
        string publishedPath;
        if (canonicalEpochRecovery)
        {
            await database.AbandonPublicationReconciliationAsync(
                reconciliation.Id,
                "publisher fixture resumes canonical epoch",
                utcCapturedAt.AddMinutes(1));
            AuthoringRunControlService runControl = new(
                store,
                new AuthoringRetryPolicy(
                    Options.Create(new ProcessingServiceOptions())));
            PreparedTicketCanonicalEpochRecoveryService canonicalRecovery =
                new(
                    database,
                    store,
                    runControl,
                    materializer,
                    new AuthoringRunSchedulerWakeSignal(),
                    NullLogger<
                        PreparedTicketCanonicalEpochRecoveryService>.Instance);
            PreparedTicketCanonicalEpochRecoveryStartResult started =
                await canonicalRecovery.StartAsync(reconciliation.Id);
            descriptor = await canonicalRecovery.RecoverAsync(
                started.Status.Run.RunId);
            publishedPath = Path.Combine(
                snapshotDirectory,
                descriptor.FileName);
        }
        else
        {
            descriptor = await recovery.RecoverAsync(reconciliation.Id);
            publishedPath = promotion.FinalPath;
        }
        if (descriptor.PublicationProof is null)
        {
            throw new InvalidOperationException(
                "The promoted reconciliation descriptor has no publication proof.");
        }
        string descriptorPath = publishedPath + ".json";
        await WriteDescriptorAsync(descriptorPath, descriptor);
        return new TicketSnapshotFixture(
            publishedPath,
            descriptorPath,
            descriptor);
    }

    public static Task<TicketSnapshotFixture>
        CreateCanonicalEpochRecoveryAsync(
            string root,
            DateTimeOffset capturedAt,
            long stableJiraGeneration)
        => CreatePromotedReconciliationAsync(
            root,
            capturedAt,
            stableJiraGeneration,
            canonicalEpochRecovery: true);

    private static PreparedTicketPayload CreateReconciliationSourcePayload(
        string ticketKey,
        DateTimeOffset savedAt)
        => new()
        {
            Key = ticketKey,
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
            SavedAt = savedAt,
        };

    private static PreparedTicketHydrationBatch
        CreateReconciliationSourceHydration(
            string ticketKey,
            DateTimeOffset capturedAt,
            long stableJiraGeneration)
        => new(
            ticketKey,
            new(
                ticketKey,
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
                capturedAt,
                "resolved",
                null,
                Reporter: "Ada Lovelace",
                Assignee: "Grace Hopper",
                SourceProject: "FHIR",
                SourceLastSuccessfulRefreshAt: capturedAt,
                SourceContentRevision: stableJiraGeneration,
                PublicDisplayNamePolicyVersion:
                    PublicDisplayNamePolicy.CurrentVersion),
            [
                new(
                    ticketKey,
                    ticketKey,
                    $"Title {ticketKey}",
                    "Triaged",
                    "Change Request",
                    "Major",
                    "Persuasive",
                    "Resolution",
                    "FHIR Infrastructure",
                    "FHIR",
                    capturedAt.AddDays(-1),
                    $"https://jira.hl7.org/browse/{ticketKey}",
                    capturedAt,
                    "resolved",
                    null,
                    Reporter: "Ada Lovelace",
                    Assignee: "Grace Hopper",
                    PublicDisplayNamePolicyVersion:
                        PublicDisplayNamePolicy.CurrentVersion),
            ],
            [],
            [],
            [],
            [],
            [
                new(
                    ticketKey,
                    "Lin Example",
                    PublicDisplayNamePolicy.CurrentVersion),
            ]);

    public async Task ChangePublicationCorpusCoordinateAsync(
        string coordinate)
    {
        AuthoringSnapshotPublicationProof proof =
            Descriptor.PublicationProof
            ?? throw new InvalidOperationException(
                "A publication proof is required.");
        string sourceRunId = proof.SourceRunId;
        string? replacementSourceRunId = null;
        await using (SqliteConnection connection = new(
            $"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            switch (coordinate)
            {
                case nameof(PreparedTicketPublicationCorpusItem.TicketKey):
                    await ExecuteAsync(
                        connection,
                        "UPDATE prepared_tickets SET Key = LOWER(Key)");
                    break;
                case nameof(PreparedTicketPublicationCorpusItem.ReceiptId):
                    await ExecuteAsync(
                        connection,
                        """
                        UPDATE authoring_run_items
                        SET AcceptedReceiptId = 'changed-' ||
                            AcceptedReceiptId
                        WHERE RunId = @sourceRunId;
                        UPDATE authoring_result_receipts
                        SET Id = 'changed-' || Id
                        WHERE RunId = @sourceRunId;
                        """,
                        ("@sourceRunId", sourceRunId));
                    break;
                case nameof(PreparedTicketPublicationCorpusItem.RunItemId):
                    await ExecuteAsync(
                        connection,
                        """
                        UPDATE authoring_result_receipts
                        SET RunItemId = 'changed-' || RunItemId
                        WHERE RunId = @sourceRunId;
                        UPDATE authoring_run_items
                        SET Id = 'changed-' || Id
                        WHERE RunId = @sourceRunId;
                        """,
                        ("@sourceRunId", sourceRunId));
                    break;
                case nameof(
                    PreparedTicketPublicationCorpusItem.ContributingRunId):
                    replacementSourceRunId =
                        $"changed-{sourceRunId}";
                    await ExecuteAsync(
                        connection,
                        """
                        UPDATE authoring_runs
                        SET Id = @replacementRunId
                        WHERE Id = @sourceRunId;
                        UPDATE authoring_run_items
                        SET RunId = @replacementRunId
                        WHERE RunId = @sourceRunId;
                        UPDATE authoring_result_receipts
                        SET RunId = @replacementRunId
                        WHERE RunId = @sourceRunId;
                        UPDATE authoring_run_input_provenance
                        SET RunId = @replacementRunId
                        WHERE RunId = @sourceRunId;
                        """,
                        ("@replacementRunId", replacementSourceRunId),
                        ("@sourceRunId", sourceRunId));
                    break;
                case nameof(PreparedTicketPublicationCorpusItem.ItemKind):
                    await ExecuteAsync(
                        connection,
                        """
                        UPDATE authoring_run_items
                        SET ItemKind = 'changed-fhir'
                        WHERE RunId = @sourceRunId
                        """,
                        ("@sourceRunId", sourceRunId));
                    break;
                case nameof(
                    PreparedTicketPublicationCorpusItem
                        .ExpectedSourceRevision):
                    await ExecuteAsync(
                        connection,
                        """
                        UPDATE authoring_run_items
                        SET ExpectedSourceRevision = 'changed-revision'
                        WHERE RunId = @sourceRunId;
                        UPDATE authoring_result_receipts
                        SET ExpectedSourceRevision = 'changed-revision',
                            ObservedSourceRevision = 'changed-revision'
                        WHERE RunId = @sourceRunId;
                        """,
                        ("@sourceRunId", sourceRunId));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(coordinate),
                        coordinate,
                        "Unknown publication corpus coordinate.");
            }
        }
        if (replacementSourceRunId is not null)
        {
            Descriptor = Descriptor with
            {
                PublicationProof = proof with
                {
                    SourceRunId = replacementSourceRunId,
                },
            };
        }
        await RefreshDescriptorHashAsync();
    }

    public async Task SetPublicationProofAsync(
        AuthoringSnapshotPublicationProof? proof)
    {
        Descriptor = Descriptor with
        {
            PublicationProof = proof,
        };
        await WriteDescriptorAsync(DescriptorPath, Descriptor);
    }

    public static async Task<TicketSnapshotFixture> CreatePreparerAsync(
        string root,
        long sequence = 1,
        string? snapshotId = null,
        bool includeSecondTicket = false,
        int schemaVersion = PreparedTicketSnapshotSchemaV1.Version,
        bool useMultipleRuns = false,
        bool includeRendererEvidence = false,
        bool includeNullProvenance = false,
        bool includeUntrustedPeople = false,
        string? firstJiraUpdatedAt = FirstJiraUpdatedAt,
        string? secondJiraUpdatedAt = SecondJiraUpdatedAt,
        DateTimeOffset? ticketSavedAt = null,
        DateTimeOffset? snapshotCreatedAt = null,
        bool includeRuntimeEvidence = false)
    {
        AuthoringSnapshotSchemaCatalog catalog =
            PreparedTicketSnapshotSchemaResolver.Resolve(schemaVersion);
        if (includeUntrustedPeople &&
            schemaVersion != PreparedTicketSnapshotSchemaV3.Version)
        {
            throw new ArgumentException(
                "Untrusted people evidence requires prepared snapshot schema v3.",
                nameof(schemaVersion));
        }
        bool hasSourceProvenance =
            schemaVersion is PreparedTicketSnapshotSchemaV2.Version or
                PreparedTicketSnapshotSchemaV3.Version;
        int? peoplePolicyVersion =
            schemaVersion == PreparedTicketSnapshotSchemaV3.Version
                ? PublicDisplayNamePolicy.CurrentVersion
                : null;
        string id = snapshotId ?? $"snapshot-{Guid.NewGuid():N}";
        string databasePath = Path.Combine(root, $"jira-fhir-{id}.db");
        string descriptorPath = databasePath + ".json";
        string runId = $"run-{id}";
        string firstRunId = includeSecondTicket && useMultipleRuns
            ? $"historical-{id}"
            : runId;
        int currentRunItemCount =
            includeSecondTicket && !useMultipleRuns ? 2 : 1;
        DateTimeOffset createdAt = snapshotCreatedAt ??
            new(2026, 9, 11, 12, 30, 0, TimeSpan.Zero);
        DateTimeOffset savedAt = ticketSavedAt ??
            new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);
        DateTimeOffset firstRefresh =
            new(2026, 9, 7, 18, 0, 0, TimeSpan.Zero);
        DateTimeOffset firstHydrationRefresh =
            new(2026, 9, 8, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset secondRefresh =
            new(2026, 9, 9, 20, 0, 0, TimeSpan.Zero);
        DateTimeOffset secondHydrationRefresh =
            new(2026, 9, 10, 23, 30, 0, TimeSpan.Zero);

        await using SqliteConnection connection = new(
            $"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.PreparerDatabase
            .EnsureSchema(connection);
        await ExecuteAsync(connection,
            """
            CREATE TABLE authoring_snapshot_provenance(SnapshotId TEXT, ProcessorKind TEXT, RunId TEXT, AuthoringEpoch INTEGER, Sequence INTEGER, SchemaVersion INTEGER, ItemCount INTEGER, ReceiptCount INTEGER, TableCountsJson TEXT, CreatedAt TEXT);

            INSERT INTO authoring_runs(
                Id, ProcessorKind, AuthoringEpoch, Status, DatabaseOnly, TotalItems,
                CreatedAt, StartedAt, CompletedAt, SnapshotId)
            VALUES(@runId, 'jira-fhir', 1, 'finalizing', 0, @currentRunItemCount,
                @createdAt, @createdAt, NULL, @snapshotId);
            INSERT INTO authoring_runs(
                Id, ProcessorKind, AuthoringEpoch, Status, DatabaseOnly, TotalItems,
                CreatedAt, StartedAt, CompletedAt, SnapshotId)
            SELECT @firstRunId, 'jira-fhir', 1, 'superseded', 0, 1,
                   @createdAt, @createdAt, @createdAt, NULL
            WHERE @firstRunId <> @runId;
            INSERT INTO authoring_run_items(
                Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision, Status,
                AcceptedReceiptId, AttemptCount, CreatedAt, StartedAt, CompletedAt)
            VALUES('item-1', @firstRunId, 'FHIR-1001', 'ticket', 'rev-1', 'complete',
                'receipt-1', 1, @createdAt, @createdAt, @createdAt);
            INSERT INTO authoring_result_receipts(
                Id, OperationId, RunId, RunItemId, BusinessKey, ContentHash,
                ExpectedSourceRevision, ObservedSourceRevision, AuthoringEpoch, PersistedAt)
            VALUES('receipt-1', 'operation-1', @firstRunId, 'item-1', 'FHIR-1001',
                'hash', 'rev-1', 'rev-1', 1, @createdAt);
            INSERT INTO prepared_tickets(
                Id, Key, RequestSummary, CommentSummary, LinkedTicketSummary,
                RelatedTicketSummary, RelatedZulipSummary, RelatedGitHubSummary,
                ExistingProposed, ProposalA, ProposalAJustification, ProposalAImpact,
                ProposalB, ProposalBJustification, ProposalBImpact, ProposalC,
                ProposalCJustification, Recommendation, RecommendationJustification, SavedAt)
            VALUES('prepared-1', 'FHIR-1001', 'Request', '', '', '', '', '', '',
                'A', 'A because', 'Non-substantive', 'B', 'B because',
                'Compatible, substantive', 'C', 'C because', 'A', 'Because', @savedAt);
            INSERT INTO prepared_ticket_hydration(
                Id, TicketKey, Priority, Resolution, ResolutionDescriptionPlain,
                Specification, RaisedInVersion, SelectedBallot, ChangeCategory,
                Impact, CommentCount, DescriptionPlain, Reporter, Assignee,
                SourceProject, SourceLastSuccessfulRefreshAt,
                SourceContentRevision, HydratedAt, HydrationStatus,
                PublicDisplayNamePolicyVersion)
            VALUES(
                'hydration-1', 'FHIR-1001', 'Major', 'Persuasive',
                'parent resolution plain', 'FHIR', '5.0.0', '2026-09',
                'Correction', 'Non-substantive', 3, 'parent request plain',
                'Ada Lovelace', 'Grace Hopper', 'FHIR',
                @firstHydrationRefresh, 111, @createdAt, 'resolved',
                @peoplePolicyVersion);
            INSERT INTO prepared_jira_hydration(
                Id, TicketKey, JiraKey, Title, Status, Type, Priority,
                Resolution, ResolutionDescriptionPlain, WorkGroup,
                WorkGroupClean, Specification, Url, Reporter, Assignee,
                HydratedAt, HydrationStatus, PublicDisplayNamePolicyVersion,
                UpdatedAt)
            VALUES(
                'jira-1', 'FHIR-1001', 'FHIR-1001', 'Snapshot title', 'Open',
                'Change Request', 'Self priority', 'Self resolution',
                'self resolution plain', 'FHIR Infrastructure',
                'FHIRInfrastructure', 'FHIR',
                'https://jira.hl7.org/browse/FHIR-1001', 'Legacy Reporter',
                'Legacy Assignee', @createdAt, 'resolved',
                @peoplePolicyVersion, @jiraUpdatedAt);
            INSERT INTO prepared_ticket_jira_content(
                TicketKey, DescriptionHtml, ResolutionDescriptionHtml)
            VALUES('FHIR-1001', '<p>request html</p>', '<p>resolution html</p>');
            INSERT INTO prepared_ticket_artifacts(TicketKey, Value)
            VALUES('FHIR-1001', 'Observation');
            INSERT INTO prepared_ticket_pages(TicketKey, Value)
            VALUES('FHIR-1001', 'patient.html');
            INSERT INTO prepared_ticket_partition_receipts(
                RunId, StageId, PartitionKey, InputFingerprint, TopicRows,
                TopicGroupRows, MemberRows, PersistedAt)
            VALUES(@runId, 'grouping', 'FHIRInfrastructure|FHIR|Change Request',
                'fingerprint-1', 0, 0, 0, @createdAt);
            INSERT INTO jira_review_workgroups(Code, Name, NameClean, UpdatedAt)
            VALUES('fhir-i', 'FHIR Infrastructure', 'FHIRInfrastructure', @createdAt);
            """,
            ("@runId", runId),
            ("@firstRunId", firstRunId),
            ("@currentRunItemCount", currentRunItemCount),
            ("@snapshotId", id),
            ("@createdAt", createdAt.ToString("O")),
            ("@savedAt", savedAt.ToString("O")),
            ("@jiraUpdatedAt", firstJiraUpdatedAt),
            ("@firstHydrationRefresh", firstHydrationRefresh.ToString("O")),
            ("@peoplePolicyVersion", peoplePolicyVersion));

        int receiptCount = 1;
        if (includeSecondTicket)
        {
            receiptCount = 2;
            await ExecuteAsync(connection,
                """
                INSERT INTO authoring_run_items(
                    Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision, Status,
                    AcceptedReceiptId, AttemptCount, CreatedAt, StartedAt, CompletedAt)
                VALUES('item-2', @runId, 'CDS-2001', 'ticket', 'rev-2', 'complete',
                    'receipt-2', 1, @createdAt, @createdAt, @createdAt);
                INSERT INTO authoring_result_receipts(
                    Id, OperationId, RunId, RunItemId, BusinessKey, ContentHash,
                    ExpectedSourceRevision, ObservedSourceRevision, AuthoringEpoch, PersistedAt)
                VALUES('receipt-2', 'operation-2', @runId, 'item-2', 'CDS-2001',
                    'hash-2', 'rev-2', 'rev-2', 1, @createdAt);
                INSERT INTO prepared_tickets(
                    Id, Key, RequestSummary, CommentSummary, LinkedTicketSummary,
                    RelatedTicketSummary, RelatedZulipSummary, RelatedGitHubSummary,
                    ExistingProposed, ProposalA, ProposalAJustification, ProposalAImpact,
                    ProposalB, ProposalBJustification, ProposalBImpact, ProposalC,
                    ProposalCJustification, Recommendation, RecommendationJustification, SavedAt)
                VALUES('prepared-2', 'CDS-2001', 'CDS request', '', '', '', '', '', '',
                    'A', 'A because', 'Non-substantive', 'B', 'B because',
                    'Compatible, substantive', 'C', 'C because', 'A', 'Because', @savedAt);
                INSERT INTO prepared_ticket_hydration(
                    Id, TicketKey, Priority, Resolution,
                    ResolutionDescriptionPlain, Specification, DescriptionPlain,
                    Reporter, Assignee, SourceProject,
                    SourceLastSuccessfulRefreshAt, SourceContentRevision,
                    HydratedAt, HydrationStatus,
                    PublicDisplayNamePolicyVersion)
                VALUES(
                    'hydration-2', 'CDS-2001', 'Minor', 'Persuasive',
                    'CDS resolution plain', 'CDS Hooks', 'CDS request plain',
                    'Katherine Johnson', 'Dorothy Vaughan', 'CDS',
                    @secondHydrationRefresh, 222, @createdAt, 'resolved',
                    @peoplePolicyVersion);
                INSERT INTO prepared_jira_hydration(
                    Id, TicketKey, JiraKey, Title, Status, Type, Priority,
                    Resolution, ResolutionDescriptionPlain, WorkGroup,
                    WorkGroupClean, Specification, Url, HydratedAt,
                    HydrationStatus, PublicDisplayNamePolicyVersion,
                    UpdatedAt)
                VALUES(
                    'jira-2', 'CDS-2001', 'CDS-2001', 'CDS title', 'Open',
                    'Change Request', 'Self CDS priority',
                    'Self CDS resolution', 'self CDS resolution plain',
                    'Clinical Decision Support', 'ClinicalDecisionSupport',
                    'CDS Hooks',
                    'https://jira.hl7.org/browse/CDS-2001', @createdAt,
                    'resolved', @peoplePolicyVersion, @jiraUpdatedAt);
                INSERT INTO prepared_ticket_partition_receipts(
                    RunId, StageId, PartitionKey, InputFingerprint, TopicRows,
                    TopicGroupRows, MemberRows, PersistedAt)
                VALUES(@runId, 'grouping', 'ClinicalDecisionSupport|CDS Hooks|Change Request',
                    'fingerprint-2', 0, 0, 0, @createdAt);
                INSERT INTO jira_review_workgroups(Code, Name, NameClean, UpdatedAt)
                VALUES('cds', 'Clinical Decision Support', 'ClinicalDecisionSupport', @createdAt);
                """,
                ("@runId", runId),
                ("@createdAt", createdAt.ToString("O")),
                ("@savedAt", savedAt.ToString("O")),
                ("@jiraUpdatedAt", secondJiraUpdatedAt),
                ("@secondHydrationRefresh", secondHydrationRefresh.ToString("O")),
                ("@peoplePolicyVersion", peoplePolicyVersion));
        }

        if (hasSourceProvenance)
        {
            DateTimeOffset? firstProvenanceRefresh = includeNullProvenance
                ? null
                : includeSecondTicket && !useMultipleRuns
                    ? secondRefresh
                    : firstRefresh;
            await ExecuteAsync(
                connection,
                """
                INSERT INTO authoring_run_input_provenance(
                    RunId, Source, LatestSuccessfulRefreshAt,
                    ContentRevision, CapturedAt)
                VALUES(
                    @firstRunId, 'jira', @firstRefresh,
                    @firstRevision, @createdAt);
                INSERT INTO authoring_run_input_provenance(
                    RunId, Source, LatestSuccessfulRefreshAt,
                    ContentRevision, CapturedAt)
                SELECT @runId, 'jira', @secondRefresh, 202, @createdAt
                WHERE @runId <> @firstRunId;
                """,
                ("@firstRunId", firstRunId),
                ("@runId", runId),
                ("@firstRefresh", firstProvenanceRefresh?.ToString("O")),
                ("@firstRevision", includeNullProvenance ? null : 101),
                ("@secondRefresh", secondRefresh.ToString("O")),
                ("@createdAt", createdAt.ToString("O")));
            await ExecuteAsync(
                connection,
                """
                INSERT INTO prepared_ticket_in_person_requesters(
                    TicketKey, DisplayName, PublicDisplayNamePolicyVersion)
                VALUES(
                    'FHIR-1001', 'Lin Example', @peoplePolicyVersion);
                """,
                ("@peoplePolicyVersion", peoplePolicyVersion));
        }

        if (includeRendererEvidence)
        {
            if (!includeSecondTicket)
            {
                throw new ArgumentException(
                    "Renderer evidence requires the second ticket.",
                    nameof(includeSecondTicket));
            }

            await ExecuteAsync(
                connection,
                """
                UPDATE prepared_tickets
                SET RequestSummary =
                    'Request FHIR-1002 and BALLOT-12',
                    CommentSummary =
                        'Comment FHIR-1004 and FHIR-1005suffix stays text',
                    LinkedTicketSummary = 'Linked FHIR-2002',
                    RelatedTicketSummary = 'Related FHIR-2002',
                    RelatedZulipSummary =
                        'Related discussion for FHIR-2003',
                    RelatedGitHubSummary =
                        'GitHub context mentioning FHIR-2004',
                    ProposalBImpact = ProposalAImpact
                WHERE Key = 'FHIR-1001';
                UPDATE prepared_jira_hydration
                SET WorkGroup = '   ',
                    WorkGroupClean = ''
                WHERE TicketKey = 'CDS-2001'
                  AND JiraKey = 'CDS-2001';

                INSERT INTO prepared_ticket_artifacts(TicketKey, Value)
                VALUES
                    ('FHIR-1001', 'observation'),
                    ('FHIR-1001', '   '),
                    ('FHIR-1001', '(unknown)'),
                    ('FHIR-1001', '__unknown__'),
                    ('FHIR-1001', '__UNKNOWN__'),
                    ('FHIR-1001', 'UnbrokenArtifactName012345678901234567890123456789');
                INSERT INTO prepared_ticket_pages(TicketKey, Value)
                VALUES
                    ('FHIR-1001', '   ');

                INSERT INTO prepared_ticket_related_jira(
                    Id, TicketKey, AssociatedTicketKey, LinkType, Justification)
                VALUES
                    ('related-jira-1', 'FHIR-1001', 'FHIR-2002', 'linked', 'linked why'),
                    ('related-jira-2', 'FHIR-1001', 'fhir-2002', 'LINKED', 'duplicate'),
                    ('related-jira-3', 'FHIR-1001', 'FHIR-2002', 'related', 'related why'),
                    ('related-jira-4', 'FHIR-1001', 'BALLOT-77', 'related', 'ballot why');
                INSERT INTO prepared_jira_hydration(
                    Id, TicketKey, JiraKey, Title, Status, Type, Resolution,
                    Url, Reporter, Assignee, HydratedAt, HydrationStatus,
                    PublicDisplayNamePolicyVersion, UpdatedAt)
                VALUES(
                    'jira-related-1', 'FHIR-1001', 'FHIR-2002',
                    'Related title', 'Resolved', 'Change Request',
                    'Persuasive', 'https://jira.example/FHIR-2002',
                    'Related Reporter', 'Related Assignee',
                    @createdAt, 'resolved', @peoplePolicyVersion,
                    '2026-09-20T10:00:00.0000000+00:00');

                INSERT INTO prepared_ticket_related_zulip(
                    Id, TicketKey, ZulipThreadId, Justification)
                VALUES
                    ('related-zulip-1', 'FHIR-1001', 'thread-1', 'zulip why'),
                    ('related-zulip-2', 'FHIR-1001', 'THREAD-1', 'duplicate');
                INSERT INTO prepared_zulip_hydration(
                    Id, TicketKey, ZulipThreadId, StreamName, Topic,
                    MessageCount, Url, HydratedAt, HydrationStatus)
                VALUES(
                    'zulip-1', 'FHIR-1001', 'thread-1', 'FHIR',
                    'Ticket discussion', 4,
                    'https://chat.fhir.org/#narrow/channel/1/topic/Ticket',
                    @createdAt, 'resolved');

                INSERT INTO prepared_ticket_related_github(
                    Id, TicketKey, GitHubItemId, Justification)
                VALUES(
                    'related-github-1', 'FHIR-1001', 'github-1', 'github why');
                INSERT INTO prepared_github_hydration(
                    Id, TicketKey, GitHubItemId, Owner, Repo, Number,
                    Title, State, IsPullRequest, Url, HydratedAt,
                    HydrationStatus)
                VALUES(
                    'github-1', 'FHIR-1001', 'github-1', 'HL7', 'fhir',
                    42, 'Issue title', 'open', 0,
                    'https://github.com/HL7/fhir/issues/42',
                    @createdAt, 'resolved');

                INSERT INTO prepared_ticket_repos(
                    Id, TicketKey, Repo, RepoCategory, Justification)
                VALUES(
                    'repo-1', 'FHIR-1001', 'HL7/fhir', 'spec', 'repo why');
                INSERT INTO prepared_repo_hydration(
                    Id, TicketKey, Repo, Description, Url, HydratedAt,
                    HydrationStatus)
                VALUES(
                    'repo-hydration-1', 'FHIR-1001', 'HL7/fhir',
                    'FHIR specification',
                    'https://github.com/HL7/fhir',
                    @createdAt, 'resolved');
                INSERT INTO prepared_ticket_jira_xref(
                    Id, TicketKey, JiraKey, Source)
                VALUES(
                    'xref-1', 'FHIR-1001', 'FHIR-2002', 'links');

                INSERT INTO prepared_ticket_topics(
                    Id, WorkGroupClean, WorkGroupDisplay, Specification,
                    Type, ShortDescription, LongerDescription,
                    RenderOrderHint, SavedAt)
                VALUES(
                    'topic-renderer', 'FHIRInfrastructure',
                    'FHIR Infrastructure', 'FHIR', 'Change Request',
                    'Renderer topic', 'Longer renderer topic', 7, @createdAt);
                INSERT INTO prepared_ticket_topic_groups(
                    Id, TopicRowId, FirstTicketKey, Rationale,
                    OrderInTopic, SavedAt)
                SELECT
                    'group-renderer', RowId, 'FHIR-1001',
                    'Discuss together', 0, @createdAt
                FROM prepared_ticket_topics
                WHERE Id = 'topic-renderer';
                INSERT INTO prepared_ticket_topic_members(
                    Id, TopicRowId, TopicGroupRowId, TicketKey,
                    OrderInContainer)
                SELECT
                    'member-renderer-1', topic.RowId, groups.RowId,
                    'FHIR-1001', 0
                FROM prepared_ticket_topics topic
                INNER JOIN prepared_ticket_topic_groups groups
                    ON groups.TopicRowId = topic.RowId
                WHERE topic.Id = 'topic-renderer';
                INSERT INTO prepared_ticket_topic_members(
                    Id, TopicRowId, TopicGroupRowId, TicketKey,
                    OrderInContainer)
                SELECT
                    'member-renderer-2', topic.RowId, groups.RowId,
                    'CDS-2001', 1
                FROM prepared_ticket_topics topic
                INNER JOIN prepared_ticket_topic_groups groups
                    ON groups.TopicRowId = topic.RowId
                WHERE topic.Id = 'topic-renderer';
                """,
                ("@createdAt", createdAt.ToString("O")),
                ("@peoplePolicyVersion", peoplePolicyVersion));

            if (hasSourceProvenance)
            {
                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO prepared_ticket_in_person_requesters(
                        TicketKey, DisplayName,
                        PublicDisplayNamePolicyVersion)
                    VALUES
                        ('FHIR-1001', '  Alan Turing  ',
                         @peoplePolicyVersion);
                    """,
                    ("@peoplePolicyVersion", peoplePolicyVersion));
            }
        }

        if (includeUntrustedPeople)
        {
            await ExecuteAsync(
                connection,
                """
                UPDATE prepared_ticket_hydration
                SET Reporter = NULL
                WHERE TicketKey = 'FHIR-1001';
                UPDATE prepared_ticket_hydration
                SET Assignee = 'Dorothy <dorothy@example.org>'
                WHERE TicketKey = 'CDS-2001';
                UPDATE prepared_jira_hydration
                SET Reporter = 'related@example.org',
                    Assignee = 'Related Stale',
                    PublicDisplayNamePolicyVersion = @oldPolicyVersion
                WHERE TicketKey = 'FHIR-1001'
                  AND JiraKey <> TicketKey;
                INSERT INTO prepared_ticket_in_person_requesters(
                    TicketKey, DisplayName, PublicDisplayNamePolicyVersion)
                VALUES
                    ('FHIR-1001', '  Trimmed Safe  ',
                     @currentPolicyVersion),
                    ('FHIR-1001', 'requester@example.org',
                     @currentPolicyVersion),
                    ('FHIR-1001', 'Missing Policy', NULL),
                    ('FHIR-1001', 'Old Policy', @oldPolicyVersion),
                    ('FHIR-1001', 'Unknown Policy', 99),
                    ('FHIR-1001', 'Future Policy', @futurePolicyVersion);
                """,
                ("@currentPolicyVersion",
                    PublicDisplayNamePolicy.CurrentVersion),
                ("@oldPolicyVersion",
                    PublicDisplayNamePolicy.CurrentVersion - 1),
                ("@futurePolicyVersion",
                    PublicDisplayNamePolicy.CurrentVersion + 1));
        }

        if (includeRuntimeEvidence)
        {
            if (!includeRendererEvidence || !includeSecondTicket ||
                useMultipleRuns || schemaVersion != PreparedTicketSnapshotSchemaV3.Version)
            {
                throw new ArgumentException(
                    "Runtime evidence requires a single-run v3 renderer fixture with two base tickets.",
                    nameof(includeRuntimeEvidence));
            }
            await AddDiscussionRuntimeEvidenceAsync(connection, runId, createdAt);
            currentRunItemCount = 4;
            receiptCount = 4;
        }

        await SanitizeSnapshotSchemaAsync(connection, catalog);
        Dictionary<string, long> counts =
            await ReadCountsAsync(connection, catalog.CountedTables);
        await ExecuteAsync(connection,
            """
            INSERT INTO authoring_snapshot_provenance
            VALUES(@snapshotId, 'jira-fhir', @runId, 1, @sequence,
                @schemaVersion, @itemCount, @receiptCount, @counts, @createdAt)
            """,
            ("@snapshotId", id),
            ("@runId", runId),
            ("@sequence", sequence),
            ("@schemaVersion", schemaVersion),
            ("@itemCount", currentRunItemCount),
            ("@receiptCount", receiptCount),
            ("@counts", JsonSerializer.Serialize(counts)),
            ("@createdAt", createdAt.ToString("O")));
        await connection.CloseAsync();
        await connection.DisposeAsync();

        AuthoringSnapshotDescriptor descriptor = await CreateDescriptorAsync(
            databasePath,
            runId,
            id,
            sequence,
            schemaVersion,
            counts,
            createdAt,
            currentRunItemCount,
            receiptCount);
        await WriteDescriptorAsync(descriptorPath, descriptor);
        return new TicketSnapshotFixture(databasePath, descriptorPath, descriptor);
    }

    public static Task<TicketSnapshotFixture> CreateDiscussionRuntimeAsync(string root)
        => CreatePreparerAsync(
            root,
            includeSecondTicket: true,
            schemaVersion: PreparedTicketSnapshotSchemaV3.Version,
            includeRendererEvidence: true,
            includeUntrustedPeople: true,
            includeRuntimeEvidence: true);

    private static async Task AddDiscussionRuntimeEvidenceAsync(
        SqliteConnection connection,
        string runId,
        DateTimeOffset createdAt)
    {
        foreach ((int number, string key, string title, string workGroup, string specification) in new[]
        {
            (3, "FHIR-1002", "Alpha fixture title", "FHIR Infrastructure", "FHIR"),
            (4, "CDS-2002", "Beta fixture title", "Clinical Decision Support", "CDS Hooks"),
        })
        {
            await ExecuteAsync(connection,
                """
                INSERT INTO authoring_run_items(
                    Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision, Status,
                    AcceptedReceiptId, AttemptCount, CreatedAt, StartedAt, CompletedAt)
                VALUES(@itemId, @runId, @key, 'ticket', 'fixture-revision', 'complete',
                    @receiptId, 1, @createdAt, @createdAt, @createdAt);
                INSERT INTO authoring_result_receipts(
                    Id, OperationId, RunId, RunItemId, BusinessKey, ContentHash,
                    ExpectedSourceRevision, ObservedSourceRevision, AuthoringEpoch, PersistedAt)
                VALUES(@receiptId, @operationId, @runId, @itemId, @key,
                    @contentHash, 'fixture-revision', 'fixture-revision', 1, @createdAt);
                INSERT INTO prepared_tickets(
                    Id, Key, RequestSummary, CommentSummary, LinkedTicketSummary,
                    RelatedTicketSummary, RelatedZulipSummary, RelatedGitHubSummary,
                    ExistingProposed, ProposalA, ProposalAJustification, ProposalAImpact,
                    ProposalB, ProposalBJustification, ProposalBImpact, ProposalC,
                    ProposalCJustification, Recommendation, RecommendationJustification, SavedAt)
                VALUES(@preparedId, @key, 'Fixture request for filtering', '', '', '', '', '', '',
                    'A', 'A because', 'Compatible, substantive', 'B', 'B because',
                    @secondImpact, 'C', 'C because', 'B', 'Fixture recommendation', @createdAt);
                INSERT INTO prepared_ticket_hydration(
                    Id, TicketKey, Specification, DescriptionPlain, Reporter, Assignee,
                    SourceProject, SourceLastSuccessfulRefreshAt, SourceContentRevision,
                    HydratedAt, HydrationStatus, PublicDisplayNamePolicyVersion)
                VALUES(@hydrationId, @key, @specification, 'Fixture source request', NULL, NULL,
                    @project, @createdAt, 222, @createdAt, 'resolved', @policyVersion);
                INSERT INTO prepared_jira_hydration(
                    Id, TicketKey, JiraKey, Title, Status, Type, WorkGroup, WorkGroupClean,
                    Specification, HydratedAt, HydrationStatus, UpdatedAt)
                VALUES(@jiraId, @key, @key, @title, 'Resolved', 'Technical Correction',
                    @workGroup, REPLACE(@workGroup, ' ', ''), @specification, @createdAt,
                    'resolved', @updatedAt);
                INSERT INTO prepared_ticket_artifacts(TicketKey, Value)
                VALUES(@key, 'Observation'), (@key, 'Patient');
                """,
                ("@itemId", $"item-{number}"),
                ("@receiptId", $"receipt-{number}"),
                ("@operationId", $"operation-{number}"),
                ("@contentHash", $"fixture-hash-{number}"),
                ("@preparedId", $"prepared-{number}"),
                ("@hydrationId", $"hydration-{number}"),
                ("@jiraId", $"jira-{number}"),
                ("@runId", runId),
                ("@key", key),
                ("@title", title),
                ("@workGroup", workGroup),
                ("@specification", specification),
                ("@project", key.Split('-')[0]),
                ("@secondImpact", number == 3 ? "Non-substantive" : "Compatible, substantive"),
                ("@policyVersion", number == 3 ? null : PublicDisplayNamePolicy.CurrentVersion),
                ("@updatedAt", $"2026-09-0{number + 4}T12:00:00.0000000+00:00"),
                ("@createdAt", createdAt.ToString("O")));
        }

        await ExecuteAsync(connection,
            """
            UPDATE authoring_runs SET TotalItems = 4 WHERE Id = @runId;
            UPDATE prepared_jira_hydration SET Title = 'Zulu snapshot title'
            WHERE TicketKey = 'FHIR-1001' AND JiraKey = TicketKey;
            UPDATE prepared_ticket_hydration SET Reporter = NULL, Assignee = NULL
            WHERE TicketKey = 'FHIR-1001';
            UPDATE prepared_ticket_jira_content
            SET DescriptionHtml = NULL, ResolutionDescriptionHtml = NULL
            WHERE TicketKey = 'FHIR-1001';
            INSERT INTO prepared_ticket_related_jira(
                Id, TicketKey, AssociatedTicketKey, LinkType, Justification)
            VALUES('runtime-jira', 'FHIR-1001', 'FHIR-1002', 'linked', 'In-corpus link justification');
            INSERT INTO prepared_ticket_repos(
                Id, TicketKey, Repo, RepoCategory, Justification)
            VALUES
                ('runtime-repo', 'FHIR-1001', 'HL7/api-incubator-ig', 'ig', 'Repository justification'),
                ('unsafe-repo', 'FHIR-1001', 'unsafe/repo', 'ig', 'Unsafe URL stays text'),
                ('missing-repo', 'FHIR-1001', 'missing/repo', 'ig', 'Missing URL stays text');
            INSERT INTO prepared_repo_hydration(
                Id, TicketKey, Repo, Description, Url, HydratedAt, HydrationStatus, HydrationReason)
            VALUES
                ('runtime-repo-hydration', 'FHIR-1001', 'HL7/api-incubator-ig',
                 'API incubator', 'https://github.com/HL7/api-incubator-ig', @createdAt, 'resolved', NULL),
                ('unsafe-repo-hydration', 'FHIR-1001', 'unsafe/repo',
                 'Unsafe source', 'javascript:alert(1)', @createdAt, 'unresolved', 'Unsafe source URL');
            INSERT INTO prepared_ticket_related_zulip(
                Id, TicketKey, ZulipThreadId, Justification)
            VALUES
                ('runtime-backed', 'FHIR-1001', '153858681', 'Retained source-backed analysis'),
                ('runtime-unverified', 'FHIR-1001', 'legacy-zero', 'Retained unverified analysis'),
                ('runtime-unsafe', 'FHIR-1001', 'bad-reference', 'Preserved invalid-reference analysis'),
                ('other-ticket-zulip', 'CDS-2001', '153858681', 'Different ticket outcome');
            INSERT INTO prepared_zulip_hydration(
                Id, TicketKey, ZulipThreadId, StreamName, Topic, MessageCount, Url,
                HydratedAt, HydrationStatus, HydrationReason)
            VALUES
                ('runtime-backed-hydration', 'FHIR-1001', '153858681', 'FHIR review',
                 'Retained topic', 5,
                 'https://chat.fhir.org/#narrow/channel/179166-implementers/topic/Retained.20topic/near/153858681',
                 @createdAt, 'unresolved', @backedReason),
                ('runtime-unverified-hydration', 'FHIR-1001', 'legacy-zero', 'Legacy channel',
                 'Zero messages', 0,
                 'https://chat.fhir.org/#narrow/channel/1/topic/Zero%20messages',
                 @createdAt, 'unresolved', @unverifiedReason),
                ('runtime-unsafe-hydration', 'FHIR-1001', 'bad-reference', NULL,
                 NULL, 0, 'data:text/html,unsafe',
                 @createdAt, 'unresolved', 'malformed thread id'),
                ('other-ticket-zulip-hydration', 'CDS-2001', '153858681', 'Other channel',
                 'Other ticket context', 10, 'https://chat.fhir.org/#narrow/channel/2/topic/Other',
                 @createdAt, 'resolved', NULL);
            INSERT INTO prepared_ticket_related_github(
                Id, TicketKey, GitHubItemId, Justification)
            VALUES('same-key-github', 'FHIR-1001', '153858681', 'Different kind outcome');
            INSERT INTO prepared_github_hydration(
                Id, TicketKey, GitHubItemId, Owner, Repo, Number, Title,
                Url, HydratedAt, HydrationStatus)
            VALUES('same-key-github-hydration', 'FHIR-1001', '153858681', 'HL7', 'fhir',
                99, 'Unrelated GitHub coordinate', 'https://github.com/HL7/fhir/issues/99',
                @createdAt, 'resolved');
            """,
            ("@runId", runId),
            ("@createdAt", createdAt.ToString("O")),
            ("@backedReason", ZulipReferenceHydrationReason.Serialize(new()
            {
                Backing = ZulipReferenceBacking.TypedResolver,
                LatestOutcome = ZulipReferenceLookupOutcome.NotFound,
                Diagnostics = [],
            })),
            ("@unverifiedReason", ZulipReferenceHydrationReason.Serialize(new()
            {
                Backing = ZulipReferenceBacking.Unverified,
                LatestOutcome = ZulipReferenceLookupOutcome.NotFound,
                Diagnostics = [],
            })));

        foreach ((string column, string body) in DiscussionRuntimeBodies)
        {
            if (column is "RequestPlain" or "ResolutionPlain")
            {
                string sourceColumn = column == "RequestPlain"
                    ? "DescriptionPlain"
                    : "ResolutionDescriptionPlain";
                await ExecuteAsync(connection,
                    $"UPDATE prepared_ticket_hydration SET {sourceColumn} = @body WHERE TicketKey = 'FHIR-1001'",
                    ("@body", body));
                if (column == "ResolutionPlain")
                {
                    await ExecuteAsync(connection,
                        """
                        UPDATE prepared_jira_hydration SET ResolutionDescriptionPlain = @body
                        WHERE TicketKey = 'FHIR-1001' AND JiraKey = TicketKey
                        """,
                        ("@body", body));
                }
            }
            else
            {
                await ExecuteAsync(connection,
                    $"UPDATE prepared_tickets SET {column} = @body WHERE Key = 'FHIR-1001'",
                    ("@body", body));
            }
        }
    }

    public static async Task<TicketSnapshotFixture> CreatePlannerAsync(
        string root,
        long sequence = 1)
    {
        string id = $"planner-{Guid.NewGuid():N}";
        string databasePath = Path.Combine(root, $"jira-fhir-{id}.db");
        string descriptorPath = databasePath + ".json";
        string runId = $"run-{id}";
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        await using SqliteConnection connection = new(
            $"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database.PlannerDatabase
            .EnsureSchema(connection);
        await ExecuteAsync(connection,
            """
            CREATE TABLE authoring_snapshot_provenance(SnapshotId TEXT, ProcessorKind TEXT, RunId TEXT, AuthoringEpoch INTEGER, Sequence INTEGER, SchemaVersion INTEGER, ItemCount INTEGER, ReceiptCount INTEGER, TableCountsJson TEXT, CreatedAt TEXT);

            INSERT INTO authoring_runs(
                Id, ProcessorKind, AuthoringEpoch, Status, DatabaseOnly, TotalItems,
                CreatedAt, StartedAt, CompletedAt, SnapshotId)
            VALUES(@runId, 'jira-fhir', 1, 'finalizing', 0, 1,
                @createdAt, @createdAt, NULL, @snapshotId);
            INSERT INTO authoring_run_items(
                Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision, Status,
                AcceptedReceiptId, AttemptCount, CreatedAt, StartedAt, CompletedAt)
            VALUES('item-1', @runId, 'FHIR-2001', 'ticket', 'rev-1', 'complete',
                'receipt-1', 1, @createdAt, @createdAt, @createdAt);
            INSERT INTO authoring_result_receipts(
                Id, OperationId, RunId, RunItemId, BusinessKey, ContentHash,
                ExpectedSourceRevision, ObservedSourceRevision, AuthoringEpoch, PersistedAt)
            VALUES('receipt-1', 'operation-1', @runId, 'item-1', 'FHIR-2001',
                'hash', 'rev-1', 'rev-1', 1, @createdAt);
            INSERT INTO planned_tickets(
                Id, Key, Resolution, ResolutionSummary, FeatureProposal, DesignRationale, SavedAt)
            VALUES('planned-1', 'FHIR-2001', 'Persuasive', 'Summary', 'Proposal',
                'Rationale', @createdAt);
            INSERT INTO planned_ticket_hydration(
                IssueKey, Specification, HydratedAt, HydrationStatus)
            VALUES('FHIR-2001', 'FHIR', @createdAt, 'resolved');
            INSERT INTO planned_jira_hydration(
                IssueKey, JiraKey, Title, Status, Type, WorkGroup, WorkGroupClean,
                Specification, HydratedAt, HydrationStatus)
            VALUES('FHIR-2001', 'FHIR-2001', 'Planner title', 'Open', 'Change Request',
                'FHIR Infrastructure', 'FHIRInfrastructure', 'FHIR', @createdAt, 'resolved');
            INSERT INTO planned_ticket_jira_content(
                TicketKey, DescriptionHtml, ResolutionDescriptionHtml)
            VALUES('FHIR-2001', '<p>planner request</p>', '<p>planner resolution</p>');
            INSERT INTO planned_ticket_partition_receipts(
                RunId, StageId, PartitionKey, InputFingerprint, TopicRows,
                TopicGroupRows, MemberRows, PersistedAt)
            VALUES(@runId, 'grouping', 'FHIRInfrastructure|FHIR|Change Request',
                'fingerprint-1', 0, 0, 0, @createdAt);
            INSERT INTO jira_review_workgroups(Code, Name, NameClean, UpdatedAt)
            VALUES('fhir-i', 'FHIR Infrastructure', 'FHIRInfrastructure', @createdAt);
            """,
            ("@runId", runId),
            ("@snapshotId", id),
            ("@createdAt", createdAt.ToString("O")));

        AuthoringSnapshotSchemaCatalog catalog =
            PlannedTicketSnapshotSchemaV1.Catalog;
        await SanitizeSnapshotSchemaAsync(connection, catalog);
        Dictionary<string, long> counts =
            await ReadCountsAsync(connection, catalog.CountedTables);
        await ExecuteAsync(connection,
            """
            INSERT INTO authoring_snapshot_provenance
            VALUES(@snapshotId, 'jira-fhir', @runId, 1, @sequence, 1, 1, 1, @counts, @createdAt)
            """,
            ("@snapshotId", id),
            ("@runId", runId),
            ("@sequence", sequence),
            ("@counts", JsonSerializer.Serialize(counts)),
            ("@createdAt", createdAt.ToString("O")));
        await connection.CloseAsync();
        await connection.DisposeAsync();

        AuthoringSnapshotDescriptor descriptor = await CreateDescriptorAsync(
            databasePath,
            runId,
            id,
            sequence,
            PlannedTicketSnapshotSchemaV1.Version,
            counts,
            createdAt,
            1,
            1);
        await WriteDescriptorAsync(descriptorPath, descriptor);
        return new TicketSnapshotFixture(databasePath, descriptorPath, descriptor);
    }

    public async Task RefreshDescriptorHashAsync()
    {
        Descriptor = Descriptor with
        {
            Sha256 = await ComputeHashAsync(DatabasePath),
            SizeBytes = new FileInfo(DatabasePath).Length,
        };
        await WriteDescriptorAsync(DescriptorPath, Descriptor);
    }

    public async Task SetTicketWorkGroupAsync(
        string ticketKey,
        string? workGroup)
    {
        await using SqliteConnection connection = new(
            $"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            """
            UPDATE prepared_jira_hydration
            SET WorkGroup = @workGroup,
                WorkGroupClean = CASE
                    WHEN @workGroup IS NULL THEN NULL
                    ELSE REPLACE(TRIM(@workGroup), ' ', '')
                END
            WHERE TicketKey = @ticketKey
              AND JiraKey = @ticketKey
            """,
            ("@workGroup", workGroup),
            ("@ticketKey", ticketKey));
        await connection.CloseAsync();
        await connection.DisposeAsync();
        await RefreshDescriptorHashAsync();
    }

    public async Task SetTicketJiraUpdatedAtAsync(
        string ticketKey,
        string? updatedAt)
    {
        await using (SqliteConnection connection = new(
            $"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(
                connection,
                """
                UPDATE prepared_jira_hydration
                SET UpdatedAt = @updatedAt
                WHERE TicketKey = @ticketKey COLLATE NOCASE
                  AND JiraKey = TicketKey COLLATE NOCASE
                """,
                ("@updatedAt", updatedAt),
                ("@ticketKey", ticketKey));
        }
        await RefreshDescriptorHashAsync();
    }

    public async Task SetTicketPublicDisplayNamesAsync(
        string ticketKey,
        string? reporter,
        string? assignee)
    {
        await using (SqliteConnection connection = new(
            $"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await ExecuteAsync(
                connection,
                """
                UPDATE prepared_ticket_hydration
                SET Reporter = @reporter, Assignee = @assignee
                WHERE TicketKey = @ticketKey COLLATE NOCASE
                """,
                ("@ticketKey", ticketKey),
                ("@reporter", reporter),
                ("@assignee", assignee));
        }
        await RefreshDescriptorHashAsync();
    }

    public static Task WriteDescriptorAsync(
        string path,
        AuthoringSnapshotDescriptor descriptor)
        => File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(descriptor, JsonOptions));

    private static async Task<AuthoringSnapshotDescriptor> CreateDescriptorAsync(
        string databasePath,
        string runId,
        string snapshotId,
        long sequence,
        int schemaVersion,
        IReadOnlyDictionary<string, long> counts,
        DateTimeOffset createdAt,
        int itemCount,
        int receiptCount,
        AuthoringSnapshotPublicationProof? publicationProof = null)
        => new(
            "jira-fhir",
            runId,
            snapshotId,
            1,
            sequence,
            schemaVersion,
            await ComputeHashAsync(databasePath),
            new FileInfo(databasePath).Length,
            itemCount,
            receiptCount,
            counts,
            Path.GetFileName(databasePath),
            createdAt,
            publicationProof);

    private static async Task<string> ComputeHashAsync(string path)
    {
        const int maxAttempts = 20;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                return Convert.ToHexString(
                        await SHA256.HashDataAsync(stream))
                    .ToLowerInvariant();
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt));
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }
    }

    private static async Task<Dictionary<string, long>> ReadCountsAsync(
        SqliteConnection connection,
        IEnumerable<string> tables)
    {
        Dictionary<string, long> result = new(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
            result[table] = Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        return result;
    }

    private static async Task SanitizeSnapshotSchemaAsync(
        SqliteConnection connection,
        AuthoringSnapshotSchemaCatalog catalog)
    {
        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF");
        List<(string Type, string Name)> objects = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT type, name
                FROM sqlite_master
                WHERE type IN ('table', 'view', 'trigger')
                  AND name NOT LIKE 'sqlite_%'
                ORDER BY type, name
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                objects.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach ((string type, string name) in objects.Where(item => item.Type != "table"))
        {
            await ExecuteAsync(
                connection,
                $"DROP {type.ToUpperInvariant()} IF EXISTS \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
        }
        foreach ((_, string name) in objects.Where(item => item.Type == "table"))
        {
            if (!catalog.Tables.Any(table => string.Equals(
                table.Name,
                name,
                StringComparison.OrdinalIgnoreCase)))
            {
                await ExecuteAsync(
                    connection,
                    $"DROP TABLE IF EXISTS \"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
            }
        }

        foreach (AuthoringSnapshotTableSchema table in catalog.Tables)
        {
            string replacement = $"__snapshot_{Guid.NewGuid():N}";
            string columnList = string.Join(
                ", ",
                table.Columns.Select(column =>
                    $"\"{column.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
            await ExecuteAsync(
                connection,
                $"""
                CREATE TABLE "{replacement}" AS
                SELECT {columnList} FROM "{table.Name}";
                DROP TABLE "{table.Name}";
                ALTER TABLE "{replacement}" RENAME TO "{table.Name}";
                """);
        }
        await ExecuteAsync(connection, "VACUUM; PRAGMA foreign_keys = ON");
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync();
    }
}
