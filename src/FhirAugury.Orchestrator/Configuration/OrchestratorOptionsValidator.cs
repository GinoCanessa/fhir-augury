using FhirAugury.Common.Api;
using Microsoft.Extensions.Options;

namespace FhirAugury.Orchestrator.Configuration;

public sealed class OrchestratorOptionsValidator : IValidateOptions<OrchestratorOptions>
{
    public ValidateOptionsResult Validate(string? name, OrchestratorOptions options)
    {
        List<string> errors = [];
        HashSet<string> knownSources = new(
            options.Services.Keys,
            StringComparer.OrdinalIgnoreCase);

        foreach ((string processingServiceName, ProcessingServiceConfig config) in options.ProcessingServices)
        {
            foreach (string requiredService in config.RequiredServices)
            {
                if (string.IsNullOrWhiteSpace(requiredService) ||
                    !knownSources.Contains(requiredService))
                {
                    errors.Add(
                        $"Processing service '{processingServiceName}' requires unknown source service '{requiredService}'.");
                }
            }

            if (!TicketWorkflowDependencyCatalog.TryGetRequiredServices(
                    processingServiceName,
                    out IReadOnlyList<string>? expected))
            {
                continue;
            }

            HashSet<string> configured = new(
                config.RequiredServices,
                StringComparer.OrdinalIgnoreCase);
            if (configured.Count != config.RequiredServices.Count ||
                configured.Count != expected.Count ||
                !configured.SetEquals(expected))
            {
                errors.Add(
                    $"Processing service '{processingServiceName}' must declare required services: {string.Join(", ", expected)}.");
            }
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
