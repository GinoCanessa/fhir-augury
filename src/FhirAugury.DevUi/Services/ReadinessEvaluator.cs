using FhirAugury.Common.Api;
using FhirAugury.DevUi.Models;

namespace FhirAugury.DevUi.Services;

public sealed class ReadinessEvaluator
{
    public TicketWorkflowReadiness Evaluate(
        ServicesStatusResponse response,
        TicketWorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(workflow);

        ServiceHealthInfo? orchestrator = Find(
            response.Services,
            "Orchestrator",
            "orchestrator");
        ServiceHealthInfo? processor = Find(
            response.Services,
            workflow.ProcessingServiceName,
            "processing");
        ServiceReadinessObservation orchestratorObservation =
            EvaluateObservation(
                "Orchestrator",
                "orchestrator",
                orchestrator);
        ServiceReadinessObservation processorObservation =
            EvaluateObservation(
                workflow.ProcessingServiceName,
                "processing",
                processor);

        IReadOnlyList<string> requiredNames =
            processor is null
                ? FhirAugury.Common.Api.TicketWorkflowDependencyCatalog
                    .GetRequiredServices(
                        workflow.ProcessingServiceName)
                : processor.RequiredServices;
        List<ServiceReadinessObservation> required = [];
        HashSet<string> seen =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string requiredName in requiredNames)
        {
            if (string.IsNullOrWhiteSpace(requiredName) ||
                !seen.Add(requiredName))
            {
                continue;
            }
            required.Add(EvaluateObservation(
                requiredName,
                "source",
                Find(response.Services, requiredName, "source")));
        }

        return new TicketWorkflowReadiness(
            workflow,
            response.LastCheckedAt,
            orchestratorObservation,
            processorObservation,
            Array.AsReadOnly(required.ToArray()));
    }

    private static ServiceHealthInfo? Find(
        IEnumerable<ServiceHealthInfo> services,
        string name,
        string kind) =>
        services.FirstOrDefault(service =>
            string.Equals(
                service.Name,
                name,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                service.ServiceKind,
                kind,
                StringComparison.OrdinalIgnoreCase));

    private static ServiceReadinessObservation EvaluateObservation(
        string name,
        string kind,
        ServiceHealthInfo? service)
    {
        if (service is null)
        {
            return new ServiceReadinessObservation(
                name,
                kind,
                ServiceReadinessState.NotObserved,
                $"{name} is not observed by the Orchestrator.",
                true,
                null,
                null);
        }

        if (!service.Configured || !service.Enabled ||
            string.Equals(
                service.Status,
                "not_configured",
                StringComparison.OrdinalIgnoreCase))
        {
            return new ServiceReadinessObservation(
                name,
                kind,
                ServiceReadinessState.Disabled,
                $"{name} is not enabled for this Orchestrator.",
                true,
                service.CheckedAt,
                service);
        }

        if (service.CheckedAt is null ||
            string.Equals(
                service.Status,
                "unobserved",
                StringComparison.OrdinalIgnoreCase))
        {
            return new ServiceReadinessObservation(
                name,
                kind,
                ServiceReadinessState.NotObserved,
                $"{name} has not been observed yet.",
                true,
                service.CheckedAt,
                service);
        }

        if (IsHealthy(service.Status))
        {
            if (string.Equals(
                    kind,
                    "processing",
                    StringComparison.OrdinalIgnoreCase) &&
                service.ProcessingIsRunning != true)
            {
                bool observedPaused =
                    service.ProcessingIsRunning == false;
                return new ServiceReadinessObservation(
                    name,
                    kind,
                    observedPaused
                        ? ServiceReadinessState.Degraded
                        : ServiceReadinessState.NotObserved,
                    observedPaused
                        ? $"{name} is reachable but processing is paused."
                        : $"{name} is reachable but its processing lifecycle was not observed.",
                    true,
                    service.CheckedAt,
                    service);
            }
            return new ServiceReadinessObservation(
                name,
                kind,
                ServiceReadinessState.Ready,
                $"{name} is ready.",
                false,
                service.CheckedAt,
                service);
        }

        if (string.Equals(
                service.Status,
                "degraded",
                StringComparison.OrdinalIgnoreCase))
        {
            return new ServiceReadinessObservation(
                name,
                kind,
                ServiceReadinessState.Degraded,
                DetailOrDefault(
                    service,
                    $"{name} is degraded."),
                true,
                service.CheckedAt,
                service);
        }

        return new ServiceReadinessObservation(
            name,
            kind,
            ServiceReadinessState.Unavailable,
            DetailOrDefault(
                service,
                $"{name} is unavailable ({service.Status})."),
            true,
            service.CheckedAt,
            service);
    }

    private static string DetailOrDefault(
        ServiceHealthInfo service,
        string defaultMessage) =>
        string.IsNullOrWhiteSpace(service.LastError)
            ? defaultMessage
            : $"{defaultMessage} {service.LastError}";

    private static bool IsHealthy(string status) =>
        string.Equals(
            status,
            "healthy",
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            status,
            "ok",
            StringComparison.OrdinalIgnoreCase);
}
