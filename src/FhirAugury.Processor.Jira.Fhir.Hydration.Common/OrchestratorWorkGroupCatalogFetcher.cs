using System.Net.Http.Json;
using FhirAugury.Common.Api;
using FhirAugury.Common.WorkGroups;

namespace FhirAugury.Processor.Jira.Fhir.Hydration.Common;

public sealed class OrchestratorWorkGroupCatalogFetcher(HttpClient httpClient)
{
    public async Task<IReadOnlyList<HydrationWorkGroupRow>> FetchAsync(
        CancellationToken ct = default)
    {
        WorkGroupListResponse? response = await httpClient.GetFromJsonAsync<WorkGroupListResponse>(
            "api/v1/github/workgroups",
            ct);
        if (response?.WorkGroups is null)
        {
            throw new InvalidOperationException("The Orchestrator returned no workgroup catalog.");
        }

        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;
        return response.WorkGroups
            .Where(workGroup => !string.IsNullOrWhiteSpace(workGroup.Code))
            .Select(workGroup => new HydrationWorkGroupRow(
                workGroup.Code,
                workGroup.Name,
                Hl7WorkGroupNameCleaner.Clean(workGroup.Name),
                updatedAt))
            .OrderBy(workGroup => workGroup.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
