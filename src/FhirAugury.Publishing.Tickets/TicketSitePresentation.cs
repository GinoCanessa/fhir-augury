using System.Globalization;

namespace FhirAugury.Publishing.Tickets;

internal sealed record TicketSitePresentation(
    int RendererSchemaVersion,
    string BaseTitle,
    string SiteName,
    DateTimeOffset? JiraSourceLastSuccessfulRefreshAt,
    ResolvedFilters Filters)
{
    public static TicketSitePresentation CreateDiscussion(
        string baseTitle,
        DateTimeOffset? jiraSourceLastSuccessfulRefreshAt,
        ResolvedFilters filters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseTitle);
        ArgumentNullException.ThrowIfNull(filters);

        DateTimeOffset? utcRefresh =
            jiraSourceLastSuccessfulRefreshAt?.ToUniversalTime();
        string freshnessSuffix = utcRefresh is null
            ? string.Empty
            : $" - Built {utcRefresh.Value.ToString(
                "MMMM dd, yyyy",
                CultureInfo.InvariantCulture)}";
        return new TicketSitePresentation(
            DiscussionRendererSchema.Version,
            baseTitle,
            $"{baseTitle}{freshnessSuffix}{filters.ToTitleSuffix()}",
            utcRefresh,
            filters);
    }
}
