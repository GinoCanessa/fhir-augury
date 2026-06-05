using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Hydration;

/// <summary>
/// Planner-side per-ticket hydration wrapper. Public seam for the
/// planner service; composes the shared
/// <see cref="HydrationCoordinator"/> over <see cref="PlannerDatabase"/>
/// (an <see cref="IHydrationTargetDatabase"/>) and
/// <see cref="OrchestratorHydrationFetcher"/>. Contract identical to
/// the preparer-side hydrator: never throws except
/// <see cref="OperationCanceledException"/>.
/// </summary>
public class PlannedTicketHydrator(
    HttpClient httpClient,
    PlannerDatabase database,
    ILogger<PlannedTicketHydrator> logger)
{
    private readonly HydrationCoordinator _coordinator = new(
        database,
        new OrchestratorHydrationFetcher(httpClient, logger),
        logger);

    public virtual Task HydrateAsync(string issueKey, CancellationToken ct)
        => _coordinator.HydrateAsync(issueKey, ct);
}
