namespace FhirAugury.Tools.TicketSite;

internal sealed record ResolvedFilters(
    string? Specification,
    string? Project,
    string? WorkGroup)
{
    public bool HasAnyFilter => Specification is not null || Project is not null || WorkGroup is not null;

    public string ToTitleSuffix()
    {
        if (!HasAnyFilter)
        {
            return string.Empty;
        }

        List<string> parts = [];
        if (Specification is not null) parts.Add($"spec={Specification}");
        if (Project is not null) parts.Add($"project={Project}");
        if (WorkGroup is not null) parts.Add($"wg={WorkGroup}");
        return $" (filtered: {string.Join(", ", parts)})";
    }
}
