using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

public sealed record GroupingMaintenanceResult(
    string RunId,
    string Status,
    int ItemCount);

public sealed class PreparedTicketGroupingMaintenanceService(
    PreparerDatabase database,
    AuthoringRunStore store,
    JiraAuthoringRunCoordinator coordinator,
    PreparedTicketRunPostProcessor postProcessor)
{
    public async Task<GroupingMaintenanceResult> RebuildAsync(
        string? runId,
        CancellationToken ct)
    {
        AuthoringRunRecord run;
        if (string.IsNullOrWhiteSpace(runId))
        {
            run = await store.CreateMaintenanceRunAsync(
                coordinator.ProcessorKind,
                (connection, token) =>
                    database.GetGroupingMaintenanceItemsAsync(
                        connection,
                        token),
                AuthoringRunPurposeValues.GroupingMaintenance,
                databaseOnly: true,
                ct: ct);
        }
        else
        {
            run = await store.GetRunAsync(runId, ct)
                ?? throw new KeyNotFoundException(
                    $"Grouping maintenance run '{runId}' was not found.");
            if (!IsGroupingMaintenanceRun(run))
            {
                throw new ArgumentException(
                    $"Run '{run.Id}' is not a grouping maintenance run.",
                    nameof(runId));
            }
        }

        if (!await store.TryAcquireMutationFenceAsync(
                coordinator.ProcessorKind,
                run.Id,
                ct: ct))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.MutationFenceUnavailable,
                "Another mutating authoring or grouping operation is active.");
        }
        await postProcessor.FinalizeRunAsync(run.Id, ct);
        AuthoringRunRecord completed = (await store.GetRunAsync(run.Id, ct))!;
        return new GroupingMaintenanceResult(
            completed.Id,
            completed.Status,
            completed.TotalItems);
    }

    internal static bool IsGroupingMaintenanceRun(AuthoringRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return string.Equals(
            run.Purpose,
            AuthoringRunPurposeValues.GroupingMaintenance,
            StringComparison.Ordinal);
    }
}
