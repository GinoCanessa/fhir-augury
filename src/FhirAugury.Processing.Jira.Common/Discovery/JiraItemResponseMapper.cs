using FhirAugury.Common.Api;

namespace FhirAugury.Processing.Jira.Common.Discovery;

public static class JiraItemResponseMapper
{
    public static JiraIssueSummaryEntry Map(ItemResponse item)
    {
        Dictionary<string, string> metadata = item.Metadata ?? [];
        metadata.TryGetValue("status", out string? status);
        metadata.TryGetValue("type", out string? type);
        metadata.TryGetValue("work_group", out string? workGroup);
        metadata.TryGetValue("specification", out string? specification);
        metadata.TryGetValue("priority", out string? priority);
        string project = item.Id.Contains('-', StringComparison.Ordinal) ? item.Id.Split('-', 2)[0] : string.Empty;
        return new JiraIssueSummaryEntry
        {
            Key = item.Id,
            ProjectKey = project,
            Title = item.Title,
            Type = type ?? string.Empty,
            Status = status ?? string.Empty,
            Priority = priority ?? string.Empty,
            WorkGroup = workGroup ?? string.Empty,
            Specification = specification ?? string.Empty,
            Url = item.Url,
            UpdatedAt = item.UpdatedAt,
        };
    }
}
