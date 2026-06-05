using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Hydration;

/// <summary>
/// Post-agent hydration step that fans out to the orchestrator's typed
/// proxies to enrich the agent-authored prepared-ticket rows with
/// human-readable Jira/Zulip/GitHub/repo facts. Owns the
/// <c>prepared_*_hydration</c> and <c>prepared_ticket_jira_xref</c> tables.
/// </summary>
/// <remarks>
/// <para>
/// Behavior preserved as the public seam for the preparer service.
/// Internally composes the shared <see cref="HydrationCoordinator"/>
/// over <see cref="PreparerDatabase"/> (an
/// <see cref="IHydrationTargetDatabase"/>) and
/// <see cref="OrchestratorHydrationFetcher"/>.
/// </para>
/// <para>
/// Contract: <see cref="HydrateAsync"/> never throws except for
/// <see cref="OperationCanceledException"/>. Per-entity failures land as
/// rows with <c>HydrationStatus = "unresolved"</c> and a short
/// <c>HydrationReason</c>; a 404/503/timeout on the parent fetch does not
/// drop child rows.
/// </para>
/// </remarks>
public class PreparedTicketHydrator(
    HttpClient httpClient,
    PreparerDatabase database,
    ILogger<PreparedTicketHydrator> logger)
{
    private readonly HydrationCoordinator _coordinator = new(
        database,
        new OrchestratorHydrationFetcher(httpClient, logger),
        logger);

    public virtual Task HydrateAsync(string ticketKey, CancellationToken ct)
        => _coordinator.HydrateAsync(ticketKey, ct);
}
