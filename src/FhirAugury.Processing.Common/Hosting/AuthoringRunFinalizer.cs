using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processing.Common.Hosting;

public sealed record AuthoringFinalizationStage(
    string Name,
    string PartitionKey,
    string InputFingerprint,
    Func<AuthoringRunStageLease, CancellationToken, Task> ExecuteAsync);

public sealed class AuthoringRunFinalizer(
    AuthoringRunStore store,
    ILogger<AuthoringRunFinalizer>? logger = null,
    TimeSpan? orphanedStageThreshold = null)
{
    private readonly ILogger<AuthoringRunFinalizer> _logger =
        logger ?? NullLogger<AuthoringRunFinalizer>.Instance;
    private readonly TimeSpan _orphanedStageThreshold =
        orphanedStageThreshold ?? TimeSpan.FromMinutes(10);

    public async Task<AuthoringSnapshotDescriptor?> FinalizeAsync(
        string runId,
        IReadOnlyCollection<AuthoringFinalizationStage> stages,
        Func<CancellationToken, Task<AuthoringSnapshotDescriptor>>? snapshotFactory = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(stages);

        AuthoringRunRecord run = await store.GetRunAsync(runId, ct)
            ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
        if (run.Status is AuthoringStatusValues.Runs.Completed or AuthoringStatusValues.Runs.CompletedDatabaseOnly)
        {
            return run.SnapshotId is null
                ? null
                : await store.GetSnapshotDescriptorAsync(run.SnapshotId, ct);
        }

        await store.MarkRunFinalizingAsync(runId, ct: ct);
        try
        {
            foreach (AuthoringFinalizationStage definition in stages)
            {
                AuthoringRunStageRecord stage = await store.EnsureRunStageAsync(
                    runId,
                    definition.Name,
                    definition.PartitionKey,
                    definition.InputFingerprint,
                    ct: ct);
                if (string.Equals(stage.Status, AuthoringStatusValues.Stages.Complete, StringComparison.Ordinal))
                {
                    continue;
                }

                AuthoringRunStageLease? lease = await store.TryStartRunStageAsync(
                    stage.Id,
                    _orphanedStageThreshold,
                    ct: ct);
                if (lease is null)
                {
                    IReadOnlyList<AuthoringRunStageRecord> refreshed = await store.GetRunStagesAsync(runId, ct);
                    AuthoringRunStageRecord current = refreshed.Single(value => value.Id == stage.Id);
                    if (string.Equals(current.Status, AuthoringStatusValues.Stages.Complete, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.StageAlreadyInProgress,
                        $"Stage '{definition.Name}' partition '{definition.PartitionKey}' is already in progress.");
                }

                try
                {
                    await definition.ExecuteAsync(lease, ct);
                    await store.CompleteRunStageAsync(stage.Id, lease.LeaseId, ct: ct);
                }
                catch (AuthoringConflictException ex)
                    when (ex.Code == AuthoringConflictCode.StageLeaseLost)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    try
                    {
                        await store.FailRunStageAsync(
                            stage.Id,
                            lease.LeaseId,
                            ex.Message,
                            ct: CancellationToken.None);
                    }
                    catch (AuthoringConflictException leaseLost)
                        when (leaseLost.Code == AuthoringConflictCode.StageLeaseLost)
                    {
                        throw;
                    }
                    throw;
                }
            }

            run = await store.GetRunAsync(runId, ct)
                ?? throw new KeyNotFoundException($"Authoring run '{runId}' was not found.");
            if (run.DatabaseOnly)
            {
                await store.CompleteRunAsync(runId, snapshotId: null, ct: ct);
                return null;
            }

            if (snapshotFactory is null)
            {
                throw new InvalidOperationException($"Run '{runId}' requires a snapshot factory.");
            }

            AuthoringSnapshotDescriptor descriptor = await snapshotFactory(ct);
            if (!string.Equals(descriptor.RunId, runId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Snapshot '{descriptor.SnapshotId}' belongs to run '{descriptor.RunId}', not '{runId}'.");
            }

            await store.CompleteRunAsync(runId, descriptor.SnapshotId, ct: ct);
            return descriptor;
        }
        catch (AuthoringConflictException ex)
            when (ex.Code is AuthoringConflictCode.StageAlreadyInProgress or AuthoringConflictCode.StageLeaseLost)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authoring run {RunId} finalization failed", runId);
            await store.MarkRunErrorAsync(runId, ex.Message, ct: CancellationToken.None);
            throw;
        }
    }
}
