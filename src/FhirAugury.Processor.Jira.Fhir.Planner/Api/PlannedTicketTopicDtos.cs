namespace FhirAugury.Processor.Jira.Fhir.Planner.Api;

public sealed record PlannedTicketTopicGroupMemberDto(string TicketKey, int Order);

public sealed record PlannedTicketTopicGroupDto(
    string FirstTicketKey,
    string Rationale,
    IReadOnlyList<PlannedTicketTopicGroupMemberDto> Members);

public sealed record PlannedTicketTopicDto(
    string Id,
    string ShortDescription,
    string LongerDescription,
    int? RenderOrderHint,
    IReadOnlyList<string> SpannedRepos,
    IReadOnlyList<PlannedTicketTopicGroupDto> LinkedTicketGroups,
    IReadOnlyList<string> RemainingTicketKeys);

public sealed record PlannedTicketTopicGroupingResponse(
    string WorkGroupClean,
    string WorkGroupDisplay,
    string Specification,
    string Type,
    DateTimeOffset SavedAt,
    IReadOnlyList<PlannedTicketTopicDto> Topics);

public sealed class PlannedTicketTopicGroupingRequest
{
    public required string WorkGroupClean { get; set; }
    public required string WorkGroupDisplay { get; set; }
    public required string Specification { get; set; }
    public required string Type { get; set; }
    public List<PlannedTicketTopicRequest> Topics { get; set; } = [];
}

public sealed class PlannedTicketTopicRequest
{
    public required string ShortDescription { get; set; }
    public required string LongerDescription { get; set; }
    public int? RenderOrderHint { get; set; }
    public List<string> SpannedRepos { get; set; } = [];
    public List<PlannedTicketTopicGroupRequest> LinkedTicketGroups { get; set; } = [];
    public List<string> RemainingTicketKeys { get; set; } = [];
}

public sealed class PlannedTicketTopicGroupRequest
{
    public required string FirstTicketKey { get; set; }
    public required string Rationale { get; set; }
    public List<PlannedTicketTopicGroupMemberRequest> Members { get; set; } = [];
}

public sealed class PlannedTicketTopicGroupMemberRequest
{
    public required string TicketKey { get; set; }
    public required int Order { get; set; }
}
