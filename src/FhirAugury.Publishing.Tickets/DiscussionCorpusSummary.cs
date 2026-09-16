using System.Globalization;
using System.Text.Json.Serialization;

namespace FhirAugury.Publishing.Tickets;

public static class DiscussionDateCoverage
{
    public const string Empty = "empty";
    public const string None = "none";
    public const string Partial = "partial";
    public const string Complete = "complete";

    internal static string FromCounts(long ticketCount, long validDateCount)
        => ticketCount == 0 ? Empty
            : validDateCount == 0 ? None
            : validDateCount == ticketCount ? Complete
            : Partial;
}

/// <summary>
/// Counts related-item rows, not tickets. A retained safe URL is not itself
/// evidence of source backing; the row's hydration reason carries that distinction.
/// </summary>
public sealed record DiscussionLinkCoverage(
    string Kind,
    long TotalRows,
    long ResolvedSafeLinks,
    long UnresolvedWithRetainedSafeLinks,
    long WithoutUsableUrl)
{
    internal static IReadOnlyList<string> Kinds { get; } =
        Array.AsReadOnly(new[] { "github", "jira", "jira-xref", "repo", "zulip" });
}

/// <summary>
/// Frozen facts about the generation-filtered public renderer projection.
/// People coverage counts tickets with a safe name, including requester coverage.
/// MaxJiraUpdatedAt is the maximum canonical self-ticket instant, not provenance.
/// </summary>
public sealed record DiscussionCorpusSummary(
    long TicketCount,
    long ExportedProjectCount,
    long ValidJiraUpdatedAtCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    DateTimeOffset? MaxJiraUpdatedAt,
    string DateCoverage,
    long TicketsWithPublicReporter,
    long TicketsWithPublicAssignee,
    long TicketsWithPublicRequester,
    IReadOnlyList<DiscussionLinkCoverage> LinksByKind);

internal static class DiscussionJiraTimestamp
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF zzz",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF'Z'",
    ];

    public static DateTimeOffset Parse(string value, string coordinate)
    {
        if (!DateTimeOffset.TryParseExact(
            value,
            Formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset timestamp))
        {
            throw new InvalidOperationException(
                $"Discussion Jira update timestamp '{coordinate}' is invalid or lacks an explicit time zone.");
        }
        return timestamp;
    }
}
