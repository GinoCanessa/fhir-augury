using System.Collections.ObjectModel;
using FhirAugury.DevUi.Models;
using FhirAugury.Publishing.Tickets;

namespace FhirAugury.DevUi.Services;

public sealed class TicketWorkflowCatalog
{
    private static readonly IReadOnlyList<TicketWorkflowDefinition>
        s_workflows = Array.AsReadOnly(
        [
            new TicketWorkflowDefinition(
                "prepare",
                "Prepare tickets",
                "Preparer",
                TicketSiteKind.Discussion,
                "Tickets for Discussion",
                "discussion"),
            new TicketWorkflowDefinition(
                "plan",
                "Plan tickets",
                "Planner",
                TicketSiteKind.Applying,
                "Tickets for Applying",
                "applying"),
        ]);

    private static readonly IReadOnlyDictionary<string, TicketWorkflowDefinition>
        s_byRoute = new ReadOnlyDictionary<
            string,
            TicketWorkflowDefinition>(
            s_workflows.ToDictionary(
                workflow => workflow.RouteKey,
                StringComparer.OrdinalIgnoreCase));

    public IReadOnlyList<TicketWorkflowDefinition> Workflows =>
        s_workflows;

    public TicketWorkflowDefinition Get(string routeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        return s_byRoute.TryGetValue(
            routeKey,
            out TicketWorkflowDefinition? workflow)
            ? workflow
            : throw new KeyNotFoundException(
                $"Unknown ticket workflow '{routeKey}'.");
    }

    public bool TryGet(
        string routeKey,
        out TicketWorkflowDefinition workflow)
    {
        if (string.IsNullOrWhiteSpace(routeKey))
        {
            workflow = null!;
            return false;
        }
        return s_byRoute.TryGetValue(routeKey, out workflow!);
    }
}
