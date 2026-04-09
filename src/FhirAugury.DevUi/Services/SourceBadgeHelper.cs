namespace FhirAugury.DevUi.Services;

/// <summary>Consistent badge CSS classes for source systems and statuses.</summary>
public static class SourceBadgeHelper
{
    public static string SourceBadgeClass(string source) => source.ToLowerInvariant() switch
    {
        "jira" => "bg-primary",
        "github" => "bg-dark",
        "zulip" => "bg-success",
        "confluence" => "bg-warning text-dark",
        "fhir" => "bg-info",
        _ => "bg-secondary",
    };

    public static string JiraStatusBadgeClass(string status) => status.ToLowerInvariant() switch
    {
        "resolved" or "done" => "bg-success",
        "open" or "reopened" => "bg-primary",
        "in progress" => "bg-info",
        "closed" => "bg-secondary",
        _ => "bg-secondary",
    };

    public static string GitHubStateBadgeClass(string state) => state.ToLowerInvariant() switch
    {
        "open" => "bg-success",
        "closed" => "bg-danger",
        "merged" => "text-bg-purple",
        _ => "bg-secondary",
    };
}
