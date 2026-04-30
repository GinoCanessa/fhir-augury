using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processor.Jira.Fhir.Applier.Configuration;
using FhirAugury.Processor.Jira.Fhir.Applier.Database;
using FhirAugury.Processor.Jira.Fhir.Applier.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Applier.Workspace;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Applier.Processing;

/// <summary>
/// Periodically refreshes per-repo primary clones and rebuilds baselines that have aged
/// past <see cref="ApplierOptions.BaselineMinSyncAge"/>. Honors
/// <see cref="ApplierOptions.BaselineSyncSchedule"/> and
/// <see cref="ApplierOptions.BaselineRefreshOnStartup"/>, gates on the shared
/// <see cref="ProcessingLifecycleService"/>, and acquires the per-repo
/// <see cref="RepoLockManager"/> so a baseline rebuild can't collide with an apply.
/// </summary>
public sealed class BaselineSyncService(
    RepoWorkspaceManager workspaceManager,
    RepoLockManager lockManager,
    RepoBaselineStore baselineStore,
    ProcessingLifecycleService lifecycle,
    IOptions<ApplierOptions> applierOptions,
    ILogger<BaselineSyncService> logger)
    : BackgroundService
{
    private readonly ApplierOptions _options = applierOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!TimeSpan.TryParse(_options.BaselineSyncSchedule, out TimeSpan interval) || interval <= TimeSpan.Zero)
        {
            logger.LogWarning("BaselineSyncSchedule '{Schedule}' is not a positive TimeSpan; baseline sync disabled.", _options.BaselineSyncSchedule);
            return;
        }

        TimeSpan minAge = TimeSpan.Zero;
        if (TimeSpan.TryParse(_options.BaselineMinSyncAge, out TimeSpan parsedMinAge) && parsedMinAge > TimeSpan.Zero)
        {
            minAge = parsedMinAge;
        }

        bool runOnStartup = _options.BaselineRefreshOnStartup;
        bool firstPass = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (lifecycle.IsRunning && (!firstPass || runOnStartup))
            {
                await SyncOnceAsync(minAge, stoppingToken).ConfigureAwait(false);
            }
            firstPass = false;

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<int> SyncOnceAsync(TimeSpan minAge, CancellationToken ct)
    {
        int rebuilt = 0;
        foreach (ApplierRepoOptions repo in _options.Repos)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using IDisposable lk = await lockManager.AcquireAsync(repo.FullName, ct).ConfigureAwait(false);
                RepoBaselineRecord? existing = await baselineStore.GetAsync(repo.FullName, ct).ConfigureAwait(false);
                if (existing is not null && minAge > TimeSpan.Zero)
                {
                    TimeSpan age = DateTimeOffset.UtcNow - existing.LastBuiltAt;
                    if (age < minAge)
                    {
                        logger.LogDebug("Skipping baseline rebuild for {Repo}: age {Age} < min {Min}", repo.FullName, age, minAge);
                        continue;
                    }
                }
                await workspaceManager.RebuildBaselineAsync(repo, ct).ConfigureAwait(false);
                rebuilt++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Baseline sync failed for {Repo}; continuing with next repo.", repo.FullName);
            }
        }
        return rebuilt;
    }
}
