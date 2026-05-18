namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;

public sealed record PreparedTicketGroupingPartition(
    string WorkGroupClean,
    string WorkGroupDisplay,
    string Specification,
    string Type,
    IReadOnlyList<PreparedTicketTopic> Topics,
    IReadOnlyList<string> IndividualTicketKeys,
    int UnattributedTicketCount,
    DateTimeOffset? LastSavedAt);

public sealed record PreparedTicketTopic(
    string Id,
    string ShortDescription,
    string LongerDescription,
    int? RenderOrderHint,
    DateTimeOffset SavedAt,
    IReadOnlyList<PreparedTicketTopicGroup> LinkedTicketGroups,
    IReadOnlyList<string> RemainingTicketKeys);

public sealed record PreparedTicketTopicGroup(
    string Id,
    string FirstTicketKey,
    string Rationale,
    int OrderInTopic,
    DateTimeOffset SavedAt,
    IReadOnlyList<PreparedTicketTopicGroupMember> Members);

public sealed record PreparedTicketTopicGroupMember(
    string TicketKey,
    int Order);

public sealed record PreparedTicketGroupingSaveResult(
    string WorkGroupClean,
    string Specification,
    string Type,
    int TopicRows,
    int TopicGroupRows,
    int MemberRows);

public sealed record PreparedTicketGroupingWorkGroupView(
    string WorkGroupClean,
    string WorkGroupDisplay,
    IReadOnlyList<PreparedTicketGroupingPartition> Partitions);
