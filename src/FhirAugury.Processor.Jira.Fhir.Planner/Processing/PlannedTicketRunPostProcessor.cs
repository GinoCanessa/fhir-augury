using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Configuration;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Processing;

public sealed class PlannedTicketRunPostProcessor(
    PlannerDatabase database,
    AuthoringRunStore authoringStore,
    AuthoringRunFinalizer finalizer,
    SqliteReviewSnapshotReconciler snapshotReconciler,
    JiraAuthoringRunCoordinator coordinator,
    OrchestratorWorkGroupCatalogFetcher workGroupFetcher,
    IPlannedTicketGroupingDispatcher groupingDispatcher,
    IOptions<PlannerServiceOptions> optionsAccessor)
    : IAuthoringRunFinalizationStrategy
{
    private readonly PlannerServiceOptions _options = optionsAccessor.Value;

    public async Task<AuthoringSnapshotDescriptor?> FinalizeRunAsync(
        string runId,
        CancellationToken ct = default)
    {
        AuthoringRunRecord run = await authoringStore.GetRunAsync(runId, ct)
            ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
        AuthoringProcessorModeRecord mode =
            await authoringStore.GetProcessorModeAsync(
                coordinator.ProcessorKind,
                ct);
        bool initialRevalidation = mode.RevalidationRequired &&
            string.Equals(
                mode.RevalidationRunId,
                runId,
                StringComparison.Ordinal);
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
            await authoringStore.SupersedeRunAsync(
                runId,
                "Canonical snapshot blocked until all legacy-unverified rows are revalidated.",
                ct: ct);
            throw new InvalidOperationException(
                "A complete legacy revalidation run is required before the first canonical snapshot.");
        }

        IReadOnlyList<AuthoringRunItemRecord> runItems =
            await authoringStore.GetRunItemsAsync(runId, ct);
        IReadOnlyList<PlannedTicketRunPartition> partitions =
            await database.GetRunPartitionsAsync(runId, ct);
        List<AuthoringFinalizationStage> stages = [];
        if (initialRevalidation)
        {
            string retirementFingerprint = AuthoringResultHasher.HashNormalizedUtf8(
                string.Join(
                    "\n",
                    runItems
                        .Where(item =>
                            item.Status == AuthoringStatusValues.Items.Superseded)
                        .OrderBy(item => item.RowId)
                        .Select(item => $"{item.Id}:{item.BusinessKey}")));
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
        stages.Add(
            new(
                "workgroup-catalog",
                "",
                AuthoringResultHasher.HashNormalizedUtf8("workgroup-catalog-v1"),
                async (lease, cancellationToken) =>
                {
                    IReadOnlyList<HydrationWorkGroupRow> workGroups =
                        await workGroupFetcher.FetchAsync(cancellationToken);
                    await database.SaveWorkGroupCatalogForRunAsync(
                        workGroups,
                        runId,
                        lease.StageId,
                        lease.LeaseId,
                        AuthoringResultHasher.HashNormalizedUtf8("workgroup-catalog-v1"),
                        cancellationToken);
                }));
        stages.AddRange(partitions.Select(partition => new AuthoringFinalizationStage(
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
                        IReadOnlyList<AuthoringRunItemRecord> items =
                            await authoringStore.GetRunItemsAsync(runId, cancellationToken);
                        IReadOnlyDictionary<string, long> counts =
                            await database.GetSnapshotTableCountsAsync(cancellationToken);
                        int receiptCount =
                            await database.GetSnapshotReceiptCountAsync(
                                runId,
                                cancellationToken);
                        SqliteReviewSnapshotWriter writer = new(database.OpenConnection, authoringStore);
                        return await writer.WriteAsync(
                            new SqliteReviewSnapshotRequest(
                                coordinator.ProcessorKind,
                                runId,
                                Path.GetFullPath(_options.SnapshotDirectory),
                                _options.SnapshotSchemaVersion,
                                items.Count(item =>
                                    item.Status is AuthoringStatusValues.Items.Complete or AuthoringStatusValues.Items.Superseded),
                                receiptCount,
                                counts,
                                new PlannedTicketSnapshotSanitizer(runId)),
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

public interface IPlannedTicketGroupingDispatcher
{
    Task ReplaceGroupingAsync(
        string runId,
        PlannedTicketRunPartition partition,
        AuthoringRunStageLease lease,
        CancellationToken ct);
}

public sealed class UnconfiguredPlannedTicketGroupingDispatcher
    : IPlannedTicketGroupingDispatcher
{
    public Task ReplaceGroupingAsync(
        string runId,
        PlannedTicketRunPartition partition,
        AuthoringRunStageLease lease,
        CancellationToken ct)
        => throw new InvalidOperationException(
            $"No grouping dispatcher is configured for partition '{partition.PartitionKey}'.");
}

public sealed class PreviewPlannedTicketGroupingDispatcher(
    IOptions<PlannerServiceOptions> optionsAccessor,
    ILogger<PreviewPlannedTicketGroupingDispatcher> logger)
    : IPlannedTicketGroupingDispatcher
{
    private readonly PlannerServiceOptions _options = optionsAccessor.Value;

    public async Task ReplaceGroupingAsync(
        string runId,
        PlannedTicketRunPartition partition,
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
                $"planner-topic-groupings exited with code {process.ExitCode}: {Tail(stderr)}");
        }
        logger.LogInformation(
            "Preview planner grouping completed for {PartitionKey}: {Output}",
            partition.PartitionKey,
            Tail(stdout));
    }

    internal ProcessStartInfo CreateStartInfo(
        string runId,
        PlannedTicketRunPartition partition,
        AuthoringRunStageLease lease)
    {
        ProcessStartInfo startInfo = new("copilot")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("/planner-topic-groupings");
        startInfo.ArgumentList.Add("--allow-all");
        AddEnvironment(startInfo, runId, partition, lease);
        return startInfo;
    }

    private void AddEnvironment(
        ProcessStartInfo startInfo,
        string runId,
        PlannedTicketRunPartition partition,
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
