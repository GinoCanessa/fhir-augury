namespace FhirAugury.Publishing.Tickets;

internal sealed record DiscussionFacetDimension(
    string Dimension,
    string Route,
    string Label,
    int SortOrder,
    bool ShowInList);

internal static class DiscussionFacetCatalog
{
    public static IReadOnlyList<DiscussionFacetDimension> Dimensions { get; } =
        Array.AsReadOnly<DiscussionFacetDimension>(
        [
            new("project", "by-project", "Project", 0, true),
            new("wg", "by-workgroup", "Workgroup", 1, true),
            new("type", "by-type", "Type", 2, true),
            new("artifact", "by-artifact", "Artifact", 3, true),
            new("page", "by-page", "Page", 4, true),
            new("impact", "by-impact", "Impact", 5, true),
            new("spec", "by-specification", "Specification", 6, true),
        ]);

    public static bool TryGet(
        string dimension,
        out DiscussionFacetDimension? definition)
    {
        definition = Dimensions.FirstOrDefault(value =>
            string.Equals(
                value.Dimension,
                dimension,
                StringComparison.Ordinal));
        return definition is not null;
    }
}
