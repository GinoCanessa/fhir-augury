using FhirAugury.Common.WorkGroups;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Api;

public sealed record PreparedTicketGroupingTopicRequest(
    string ShortDescription,
    string LongerDescription,
    int? RenderOrderHint,
    IReadOnlyList<PreparedTicketGroupingLinkedGroupRequest> LinkedTicketGroups,
    IReadOnlyList<string> RemainingTicketKeys);

public sealed record PreparedTicketGroupingLinkedGroupRequest(
    string FirstTicketKey,
    string Rationale,
    IReadOnlyList<PreparedTicketGroupingMemberRequest> Members);

public sealed record PreparedTicketGroupingMemberRequest(string TicketKey, int Order);

public sealed record PreparedTicketGroupingPutRequest(
    string WorkGroupDisplay,
    IReadOnlyList<PreparedTicketGroupingTopicRequest> Topics);

public sealed record PreparedTicketGroupingMemberDto(string TicketKey, int Order);

public sealed record PreparedTicketGroupingLinkedGroupDto(
    string Id,
    string FirstTicketKey,
    string Rationale,
    int OrderInTopic,
    DateTimeOffset SavedAt,
    IReadOnlyList<PreparedTicketGroupingMemberDto> Members);

public sealed record PreparedTicketGroupingTopicDto(
    string Id,
    string ShortDescription,
    string LongerDescription,
    int? RenderOrderHint,
    DateTimeOffset SavedAt,
    IReadOnlyList<PreparedTicketGroupingLinkedGroupDto> LinkedTicketGroups,
    IReadOnlyList<string> RemainingTicketKeys);

public sealed record PreparedTicketGroupingPartitionDto(
    string WorkGroupClean,
    string WorkGroupDisplay,
    string Specification,
    string Type,
    IReadOnlyList<PreparedTicketGroupingTopicDto> Topics,
    IReadOnlyList<string> IndividualTicketKeys,
    int UnattributedTicketCount,
    DateTimeOffset? LastSavedAt);

public sealed record PreparedTicketGroupingWorkGroupDto(
    string WorkGroupClean,
    string WorkGroupDisplay,
    IReadOnlyList<PreparedTicketGroupingPartitionDto> Partitions);

public sealed record PreparedTicketGroupingSaveResultDto(
    string WorkGroupClean,
    string Specification,
    string Type,
    int TopicRows,
    int TopicGroupRows,
    int MemberRows);

public static class PreparedTicketGroupingDtoMapper
{
    /// <summary>
    /// Builds the persistence payload from the HTTP request. The
    /// <paramref name="workGroupClean"/> argument may arrive in any of
    /// <c>name</c> / <c>nameClean</c> / <c>code</c> form, or as the
    /// canonical cleaner slug already — the mapper normalises it via
    /// <see cref="Hl7WorkGroupNameCleaner.Clean(string?)"/> defensively so
    /// the persisted column is always in the canonical form. The
    /// <c>code</c> form is preserved as-is when the cleaner produces an
    /// empty string; resolution from short codes (e.g. <c>"oo"</c> ->
    /// <c>"OrdersAndObservations"</c>) is the responsibility of the
    /// orchestrator / CLI / MCP layer where the HL7 catalog is available.
    /// </summary>
    public static PreparedTicketGroupingPayload ToPayload(
        string workGroupClean,
        string specification,
        string type,
        PreparedTicketGroupingPutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string cleaned = Hl7WorkGroupNameCleaner.Clean(workGroupClean);
        string canonical = string.IsNullOrEmpty(cleaned) ? workGroupClean : cleaned;
        return new PreparedTicketGroupingPayload
        {
            WorkGroupClean = canonical,
            WorkGroupDisplay = request.WorkGroupDisplay,
            Specification = specification,
            Type = type,
            Topics = request.Topics?.Select(ToPayload).ToList() ?? [],
        };
    }

    private static PreparedTicketTopicPayload ToPayload(PreparedTicketGroupingTopicRequest topic) => new()
    {
        ShortDescription = topic.ShortDescription,
        LongerDescription = topic.LongerDescription,
        RenderOrderHint = topic.RenderOrderHint,
        LinkedTicketGroups = topic.LinkedTicketGroups?.Select(ToPayload).ToList() ?? [],
        RemainingTicketKeys = topic.RemainingTicketKeys?.ToList() ?? [],
    };

    private static PreparedTicketTopicGroupPayload ToPayload(PreparedTicketGroupingLinkedGroupRequest group) => new()
    {
        FirstTicketKey = group.FirstTicketKey,
        Rationale = group.Rationale,
        Members = group.Members?.Select(m => new PreparedTicketTopicGroupMemberPayload
        {
            TicketKey = m.TicketKey,
            Order = m.Order,
        }).ToList() ?? [],
    };

    public static PreparedTicketGroupingPartitionDto ToDto(PreparedTicketGroupingPartition partition) => new(
        partition.WorkGroupClean,
        partition.WorkGroupDisplay,
        partition.Specification,
        partition.Type,
        partition.Topics.Select(ToDto).ToArray(),
        partition.IndividualTicketKeys.ToArray(),
        partition.UnattributedTicketCount,
        partition.LastSavedAt);

    public static PreparedTicketGroupingWorkGroupDto ToDto(PreparedTicketGroupingWorkGroupView view) => new(
        view.WorkGroupClean,
        view.WorkGroupDisplay,
        view.Partitions.Select(ToDto).ToArray());

    public static PreparedTicketGroupingSaveResultDto ToDto(PreparedTicketGroupingSaveResult result) => new(
        result.WorkGroupClean,
        result.Specification,
        result.Type,
        result.TopicRows,
        result.TopicGroupRows,
        result.MemberRows);

    private static PreparedTicketGroupingTopicDto ToDto(PreparedTicketTopic topic) => new(
        topic.Id,
        topic.ShortDescription,
        topic.LongerDescription,
        topic.RenderOrderHint,
        topic.SavedAt,
        topic.LinkedTicketGroups.Select(ToDto).ToArray(),
        topic.RemainingTicketKeys.ToArray());

    private static PreparedTicketGroupingLinkedGroupDto ToDto(PreparedTicketTopicGroup group) => new(
        group.Id,
        group.FirstTicketKey,
        group.Rationale,
        group.OrderInTopic,
        group.SavedAt,
        group.Members.Select(m => new PreparedTicketGroupingMemberDto(m.TicketKey, m.Order)).ToArray());
}
