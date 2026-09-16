using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

public sealed class PreparedTicketRunPostProcessor(
    PreparerDatabase database,
    AuthoringRunStore authoringStore,
    AuthoringRunFinalizer finalizer,
    SqliteReviewSnapshotReconciler snapshotReconciler,
    JiraAuthoringRunCoordinator coordinator,
    OrchestratorWorkGroupCatalogFetcher workGroupFetcher,
    IPreparedTicketGroupingDispatcher groupingDispatcher,
    IOptions<PreparerServiceOptions> optionsAccessor,
    PreparedTicketPublicationRefreshService? publicationRefreshService = null,
    PreparedTicketSnapshotMaterializer? snapshotMaterializer = null)
    : IAuthoringRunFinalizationStrategy
{
    private readonly PreparerServiceOptions _options = optionsAccessor.Value;
    private readonly PreparedTicketSnapshotMaterializer
        _snapshotMaterializer = snapshotMaterializer ??
            new PreparedTicketSnapshotMaterializer(
                database,
                authoringStore,
                snapshotReconciler,
                optionsAccessor);

    public async Task<AuthoringSnapshotDescriptor?> FinalizeRunAsync(
        string runId,
        CancellationToken ct = default)
    {
        AuthoringRunRecord run = await authoringStore.GetRunAsync(runId, ct)
            ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
        bool publicationRefresh = run.Purpose == AuthoringRunPurposeValues.PublicationRefresh;
        if (publicationRefresh && run.Status == AuthoringStatusValues.Runs.Completed)
        {
            return run.SnapshotId is null ? null :
                await authoringStore.GetSnapshotDescriptorAsync(run.SnapshotId, ct);
        }
        if (publicationRefresh && run.Status == AuthoringStatusValues.Runs.Superseded)
        {
            return null;
        }
        try
        {
            return await FinalizeRunCoreAsync(run, ct);
        }
        catch (PreparedTicketPublicationProtectionException ex) when (publicationRefresh)
        {
            return await RefusePublicationRefreshAsync(runId, $"{ex.FailureCode}: {ex.Message}");
        }
        catch (AuthoringConflictException ex)
            when (publicationRefresh && ex.Code is
                AuthoringConflictCode.SourceRevisionMismatch or AuthoringConflictCode.StageFingerprintMismatch)
        {
            return await RefusePublicationRefreshAsync(runId, ex.Message);
        }
    }

    private async Task<AuthoringSnapshotDescriptor?> RefusePublicationRefreshAsync(
        string runId,
        string reason)
    {
        AuthoringRunRecord run = await authoringStore.GetRunAsync(runId, CancellationToken.None)
            ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
        if (run.Status == AuthoringStatusValues.Runs.Finalizing)
        {
            await authoringStore.MarkRunErrorAsync(runId, reason, ct: CancellationToken.None);
        }
        await authoringStore.SupersedeRunAsync(
            runId,
            $"{reason} Retain the existing publication and inspect the conflict; publication repair does not re-author or regroup tickets.",
            ct: CancellationToken.None);
        return null;
    }

    private async Task<AuthoringSnapshotDescriptor?> FinalizeRunCoreAsync(
        AuthoringRunRecord run,
        CancellationToken ct)
    {
        string runId = run.Id;
        bool initialRevalidation = string.Equals(
            run.Purpose,
            AuthoringRunPurposeValues.InitialRevalidation,
            StringComparison.Ordinal);
        bool groupingMaintenance = string.Equals(
            run.Purpose,
            AuthoringRunPurposeValues.GroupingMaintenance,
            StringComparison.Ordinal);
        bool publicationRefresh = string.Equals(
            run.Purpose,
            AuthoringRunPurposeValues.PublicationRefresh,
            StringComparison.Ordinal);
        if (!initialRevalidation &&
            !groupingMaintenance &&
            !publicationRefresh &&
            !string.Equals(
                run.Purpose,
                AuthoringRunPurposeValues.Authoring,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Authoring run '{runId}' has unsupported purpose '{run.Purpose}'.");
        }
        if (publicationRefresh &&
            _options.SnapshotSchemaVersion !=
                PreparedTicketSnapshotSchemaV3.Version)
        {
            throw new PreparedTicketPublicationProtectionException(
                PreparedTicketPublicationRefreshFailureCodes.UnsupportedSnapshotSchema,
                "Publication refresh runs require Preparer snapshot schema v3.");
        }
        AuthoringSnapshotSchemaCatalog snapshotSchema =
            PreparedTicketSnapshotSchemaResolver.Resolve(_options.SnapshotSchemaVersion);
        PreparedTicketPublicationEnrichmentInput? enrichmentInput =
            publicationRefresh ? PreparerDatabase.ReadPublicationEnrichmentInput(run) : null;
        if (enrichmentInput is not null)
        {
            await database.ValidatePublicationEnrichmentAsync(runId, enrichmentInput, ct);
        }

        AuthoringProcessorModeRecord mode =
            await authoringStore.GetProcessorModeAsync(
                coordinator.ProcessorKind,
                ct);
        if (initialRevalidation &&
            (!mode.RevalidationRequired ||
             !string.Equals(
                 mode.RevalidationRunId,
                 runId,
                 StringComparison.Ordinal)))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.RunNotActive,
                $"Initial revalidation run '{runId}' is no longer the active revalidation coordinate.");
        }
        if (initialRevalidation &&
            await coordinator.SupersedeStaleItemsAsync(runId, ct))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.SourceRevisionMismatch,
                $"Initial revalidation run '{runId}' was replaced because source revisions changed before finalization.");
        }
        int blockingLegacyRows = initialRevalidation
            ? await database.CountLegacyUnverifiedNotSupersededByRunAsync(
                runId,
                ct)
            : await database.CountLegacyUnverifiedAsync(ct);
        if (!run.DatabaseOnly && blockingLegacyRows > 0)
        {
            if (publicationRefresh)
            {
                throw new PreparedTicketPublicationProtectionException(
                    PreparedTicketPublicationRefreshFailureCodes.InvalidAcceptedGraph,
                    "The canonical corpus contains legacy-unverified output and cannot be enriched.");
            }
            await authoringStore.SupersedeRunAsync(
                runId,
                "Canonical snapshot blocked until all legacy-unverified rows are revalidated.",
                ct: ct);
            throw new InvalidOperationException(
                "A complete legacy revalidation run is required before the first canonical snapshot.");
        }

        IReadOnlyList<PreparedTicketRunPartition> partitions =
            await database.GetRunPartitionsAsync(runId, ct);
        List<AuthoringFinalizationStage> stages = [];
        PreparedTicketPublicationRefreshInventory? refreshInventory = null;
        string? refreshInputFingerprint = null;
        string refreshStageName = enrichmentInput is null
            ? PreparerDatabase.PublicationMetadataStageName
            : PreparedTicketPublicationEnrichmentContract.StageName;
        if (initialRevalidation)
        {
            IReadOnlyList<AuthoringRunItemRecord> supersededItems =
                await authoringStore.GetRevalidationSupersededItemsAsync(
                    runId,
                    ct);
            string retirementFingerprint = AuthoringResultHasher.HashNormalizedUtf8(
                string.Join(
                    "\n",
                    supersededItems.Select(item =>
                        $"{item.RunId}:{item.Id}:{item.BusinessKey}:{item.ItemKind}:{item.ExpectedSourceRevision}")));
            stages.Add(new AuthoringFinalizationStage(
                "revalidation-retirement",
                "",
                retirementFingerprint,
                (lease, cancellationToken) =>
                    database.RetireSupersededLegacyRowsAsync(
                        runId,
                        lease,
                        retirementFingerprint,
                        cancellationToken)));
        }
        if (publicationRefresh)
        {
            PreparedTicketPublicationRefreshService refreshService =
                publicationRefreshService ??
                throw new InvalidOperationException(
                    "Publication refresh finalization is not configured.");
            refreshInventory = enrichmentInput is null
                ? await database.GetPublicationRefreshInventoryAsync(ct)
                : PreparedTicketPublicationRefreshInventory.FromEnrichmentInput(enrichmentInput);
            await refreshService.ValidateRunInventoryAsync(
                run,
                refreshInventory,
                ct);
            refreshInputFingerprint = enrichmentInput is null
                ? PreparedTicketPublicationContract
                    .ComputePublicationRefreshInputFingerprint(
                        run.SourceRunId!,
                        refreshInventory.Candidates.Select(candidate =>
                            candidate.ToPublicationCorpusItem()))
                : PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(enrichmentInput);
            PreparedTicketPublicationRefreshInventory capturedInventory =
                refreshInventory;
            string capturedInputFingerprint = refreshInputFingerprint;
            stages.Add(
                new AuthoringFinalizationStage(
                    refreshStageName,
                    string.Empty,
                    capturedInputFingerprint,
                    (lease, cancellationToken) =>
                        enrichmentInput is null
                            ? refreshService.ExecuteMetadataStageAsync(
                                run, lease, capturedInputFingerprint, capturedInventory, cancellationToken)
                            : refreshService.ExecuteEnrichmentStageAsync(
                                run, lease, capturedInputFingerprint, enrichmentInput, cancellationToken)));
            stages.AddRange(
                partitions.Select(partition =>
                    new AuthoringFinalizationStage(
                        PreparerDatabase.GroupingCertificationStageName,
                        partition.PartitionKey,
                        partition.InputFingerprint,
                        async (lease, cancellationToken) =>
                            _ = await database
                                .CertifyGroupingPartitionAsync(
                                    runId,
                                    lease,
                                    partition,
                                    cancellationToken))));
        }
        else
        {
            stages.Add(
                new(
                    "workgroup-catalog",
                    "",
                    AuthoringResultHasher.HashNormalizedUtf8(
                        "workgroup-catalog-v1"),
                    async (lease, cancellationToken) =>
                    {
                        IReadOnlyList<HydrationWorkGroupRow> workGroups =
                            await workGroupFetcher.FetchAsync(
                                cancellationToken);
                        await database.SaveWorkGroupCatalogForRunAsync(
                            workGroups,
                            runId,
                            lease.StageId,
                            lease.LeaseId,
                            AuthoringResultHasher.HashNormalizedUtf8(
                                "workgroup-catalog-v1"),
                            cancellationToken);
                    }));
            stages.AddRange(
                partitions.Select(partition =>
                    new AuthoringFinalizationStage(
                        "grouping",
                        partition.PartitionKey,
                        partition.InputFingerprint,
                        async (lease, cancellationToken) =>
                        {
                            await groupingDispatcher.ReplaceGroupingAsync(
                                runId,
                                partition,
                                lease,
                                cancellationToken);
                            _ = await database.GetGroupingReceiptAsync(
                                    runId,
                                    lease.StageId,
                                    partition.PartitionKey,
                                    partition.InputFingerprint,
                                    cancellationToken)
                                ?? throw new InvalidOperationException(
                                    $"Grouping partition '{partition.PartitionKey}' completed without a durable receipt.");
                        })));
        }

        Func<SqliteConnection, CancellationToken, Task>? completionGuard =
            initialRevalidation
                ? (connection, cancellationToken) =>
                    JiraProcessingSourceTicketStore
                        .EnsureRunSourceRevisionsCurrentAsync(
                            connection,
                            runId,
                            cancellationToken)
                : null;
        try
        {
            return await finalizer.FinalizeAsync(
                runId,
                stages,
                run.DatabaseOnly
                    ? null
                    : async cancellationToken =>
                    {
                        if (!publicationRefresh)
                        {
                            return await _snapshotMaterializer
                                .MaterializeAsync(
                                    run,
                                    snapshotSchema,
                                    ct: cancellationToken);
                        }

                        IReadOnlyList<AuthoringRunStageRecord> completedStages =
                            await authoringStore.GetRunStagesAsync(
                                runId,
                                cancellationToken);
                        AuthoringRunStageRecord metadataStage =
                            completedStages.Single(stage =>
                                string.Equals(
                                    stage.StageName,
                                    refreshStageName,
                                    StringComparison.Ordinal) &&
                                string.Equals(
                                    stage.PartitionKey,
                                    string.Empty,
                                    StringComparison.Ordinal) &&
                                string.Equals(
                                    stage.InputFingerprint,
                                    refreshInputFingerprint,
                                    StringComparison.Ordinal) &&
                                string.Equals(
                                    stage.Status,
                                    AuthoringStatusValues.Stages.Complete,
                                    StringComparison.Ordinal));
                        PreparedTicketPublicationRefreshReceiptRecord
                            metadataReceipt =
                                await database
                                    .GetMatchingPublicationRefreshReceiptAsync(
                                        runId,
                                        metadataStage.Id,
                                        refreshInputFingerprint!,
                                        refreshInventory!.CorpusFingerprint,
                                        cancellationToken)
                                ?? throw new InvalidOperationException(
                                    "Publication metadata stage completed without a durable apply receipt.");
                        IReadOnlyList<
                            PreparedTicketGroupingCertificationEvidence>
                            groupingCertifications =
                                await database
                                    .GetPublicationGroupingCertificationsAsync(
                                        runId,
                                        partitions,
                                        cancellationToken);
                        string groupingFingerprint =
                            PreparedTicketPublicationContract
                                .ComputeGroupingFingerprint(
                                    groupingCertifications.Select(
                                        certification =>
                                            new PreparedTicketPublicationGroupingPartition(
                                                certification.PartitionKey,
                                                certification
                                                    .OutputFingerprint)));
                        DateTimeOffset proofCapturedAt =
                            completedStages
                                .Select(stage => stage.CompletedAt)
                                .OfType<DateTimeOffset>()
                                .Append(metadataReceipt.AppliedAt)
                                .Max()
                                .ToUniversalTime();
                        AuthoringSnapshotPublicationProof proof = new(
                            PreparedTicketPublicationContract
                                .CurrentVersion,
                            PreparedTicketPublicationContract
                                .PublicationRefreshPurpose,
                            run.SourceRunId!,
                            PreparedTicketPublicationContract
                                .JiraSourceName,
                            metadataReceipt
                                .SourceLastSuccessfulRefreshAt
                                .ToUniversalTime(),
                            metadataReceipt.SourceContentRevision,
                            metadataReceipt
                                .PublicDisplayNamePolicyVersion,
                            metadataReceipt.CorpusFingerprint,
                            groupingFingerprint,
                            proofCapturedAt);
                        return await _snapshotMaterializer.MaterializeAsync(
                            run,
                            snapshotSchema,
                            proof,
                            groupingCertifications,
                            cancellationToken);
                    },
                completionGuard,
                ct);
        }
        catch (AuthoringConflictException ex)
            when (initialRevalidation &&
                  ex.Code == AuthoringConflictCode.SourceRevisionMismatch)
        {
            if (await coordinator.SupersedeStaleItemsAsync(runId, ct))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.SourceRevisionMismatch,
                    $"Initial revalidation run '{runId}' was replaced because source revisions changed during finalization.");
            }
            throw;
        }
    }

    public Task ReconcileSnapshotsOnStartupAsync(CancellationToken ct)
        => snapshotReconciler.ReconcileAsync(ct);

    async Task IAuthoringRunFinalizationStrategy.FinalizeRunAsync(
        string runId,
        CancellationToken ct)
        => _ = await FinalizeRunAsync(runId, ct);
}

public interface IPreparedTicketGroupingDispatcher
{
    Task ReplaceGroupingAsync(
        string runId,
        PreparedTicketRunPartition partition,
        AuthoringRunStageLease lease,
        CancellationToken ct);
}

public sealed class UnconfiguredPreparedTicketGroupingDispatcher
    : IPreparedTicketGroupingDispatcher
{
    public Task ReplaceGroupingAsync(
        string runId,
        PreparedTicketRunPartition partition,
        AuthoringRunStageLease lease,
        CancellationToken ct)
        => throw new InvalidOperationException(
            $"No grouping dispatcher is configured for partition '{partition.PartitionKey}'.");
}

public sealed class PreviewPreparedTicketGroupingDispatcher(
    IOptions<PreparerServiceOptions> optionsAccessor,
    ILogger<PreviewPreparedTicketGroupingDispatcher> logger)
    : IPreparedTicketGroupingDispatcher
{
    private readonly PreparerServiceOptions _options = optionsAccessor.Value;

    public async Task ReplaceGroupingAsync(
        string runId,
        PreparedTicketRunPartition partition,
        AuthoringRunStageLease lease,
        CancellationToken ct)
    {
        ProcessStartInfo startInfo = CreateStartInfo(runId, partition, lease);

        using Process process = new() { StartInfo = startInfo };
        process.Start();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"topic-groupings exited with code {process.ExitCode}: {Tail(stderr)}");
        }
        logger.LogInformation(
            "Preview grouping completed for {PartitionKey}: {Output}",
            partition.PartitionKey,
            Tail(stdout));
    }

    internal ProcessStartInfo CreateStartInfo(
        string runId,
        PreparedTicketRunPartition partition,
        AuthoringRunStageLease lease)
    {
        ProcessStartInfo startInfo = new("copilot")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("/topic-groupings");
        startInfo.ArgumentList.Add("--allow-all");
        AddEnvironment(startInfo, runId, partition, lease);
        return startInfo;
    }

    private void AddEnvironment(
        ProcessStartInfo startInfo,
        string runId,
        PreparedTicketRunPartition partition,
        AuthoringRunStageLease lease)
    {
        startInfo.Environment["FHIR_AUGURY_GROUPING_WORKER"] = "1";
        startInfo.Environment["FHIR_AUGURY_GROUPING_PROCESSOR_URL"] =
            $"http://localhost:{_options.Ports.Http}";
        startInfo.Environment["FHIR_AUGURY_GROUPING_RUN_ID"] = runId;
        startInfo.Environment["FHIR_AUGURY_GROUPING_STAGE_ID"] = lease.StageId;
        startInfo.Environment["FHIR_AUGURY_GROUPING_STAGE_LEASE_ID"] = lease.LeaseId;
        startInfo.Environment["FHIR_AUGURY_GROUPING_INPUT_FINGERPRINT"] =
            partition.InputFingerprint;
        startInfo.Environment["FHIR_AUGURY_GROUPING_WORK_GROUP_CLEAN"] =
            partition.WorkGroupClean;
        startInfo.Environment["FHIR_AUGURY_GROUPING_SPECIFICATION"] =
            partition.Specification;
        startInfo.Environment["FHIR_AUGURY_GROUPING_TYPE"] = partition.Type;
    }

    private static string Tail(string value)
        => value.Length <= 4096 ? value : value[^4096..];
}
