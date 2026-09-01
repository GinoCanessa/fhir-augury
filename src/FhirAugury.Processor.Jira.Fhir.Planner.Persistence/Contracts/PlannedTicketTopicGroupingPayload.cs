namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;

/// <summary>
/// Wire shape of a planner topic grouping write. Mirrors the preparer
/// grouping payload shape but (a) carries no Recommendation field (the
/// planner has no recommendation concept) and (b) has
/// <see cref="PlannedTicketTopicPayload.SpannedRepos"/> as a
/// first-class topic-level field — the coordinated repo set persisted
/// to <c>planned_ticket_topic_repos</c>.
/// </summary>
public sealed class PlannedTicketTopicGroupingPayload
{
    public required string WorkGroupClean { get; set; }
    public required string WorkGroupDisplay { get; set; }
    public required string Specification { get; set; }
    public required string Type { get; set; }
    public DateTimeOffset? SavedAt { get; set; }
    public List<PlannedTicketTopicPayload> Topics { get; set; } = [];
}

public sealed class PlannedTicketTopicPayload
{
    public required string ShortDescription { get; set; }
    public required string LongerDescription { get; set; }
    public int? RenderOrderHint { get; set; }
    public List<string> SpannedRepos { get; set; } = [];
    public List<PlannedTicketTopicGroupPayload> LinkedTicketGroups { get; set; } = [];
    public List<string> RemainingTicketKeys { get; set; } = [];
}

public sealed class PlannedTicketTopicGroupPayload
{
    public required string FirstTicketKey { get; set; }
    public required string Rationale { get; set; }
    public List<PlannedTicketTopicGroupMemberPayload> Members { get; set; } = [];
}

public sealed class PlannedTicketTopicGroupMemberPayload
{
    public required string TicketKey { get; set; }
    public required int Order { get; set; }
}
