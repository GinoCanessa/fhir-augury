using System.Collections.ObjectModel;

namespace FhirAugury.Common.Api;

/// <summary>Fixed source-service dependencies for ticket authoring workflows.</summary>
public static class TicketWorkflowDependencyCatalog
{
    public const string PreparerServiceName = "Preparer";
    public const string PlannerServiceName = "Planner";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> s_requiredServices =
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [PreparerServiceName] = Array.AsReadOnly(["Jira"]),
                [PlannerServiceName] = Array.AsReadOnly(["Jira", "GitHub"]),
            });

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> RequiredServices =>
        s_requiredServices;

    public static bool TryGetRequiredServices(
        string processingServiceName,
        out IReadOnlyList<string> requiredServices) =>
        s_requiredServices.TryGetValue(processingServiceName, out requiredServices!);

    public static IReadOnlyList<string> GetRequiredServices(string processingServiceName) =>
        TryGetRequiredServices(processingServiceName, out IReadOnlyList<string>? requiredServices)
            ? requiredServices
            : throw new KeyNotFoundException(
                $"Processing service '{processingServiceName}' is not a ticket workflow.");
}
