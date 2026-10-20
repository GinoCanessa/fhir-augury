namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

public sealed class PreparedTicketGroupingPayload
{
    public required string WorkGroupClean { get; set; }
    public required string WorkGroupDisplay { get; set; }
    public required string Specification { get; set; }
    public required string Type { get; set; }
    public DateTimeOffset? SavedAt { get; set; }
    public List<PreparedTicketTopicPayload> Topics { get; set; } = [];
}

public sealed class PreparedTicketTopicPayload
{
    public required string ShortDescription { get; set; }
    public required string LongerDescription { get; set; }
    public int? RenderOrderHint { get; set; }
    public List<PreparedTicketTopicGroupPayload> LinkedTicketGroups { get; set; } = [];
    public List<string> RemainingTicketKeys { get; set; } = [];
}

public sealed class PreparedTicketTopicGroupPayload
{
    public required string FirstTicketKey { get; set; }
    public required string Rationale { get; set; }
    public List<PreparedTicketTopicGroupMemberPayload> Members { get; set; } = [];
}

public sealed class PreparedTicketTopicGroupMemberPayload
{
    public required string TicketKey { get; set; }
    public required int Order { get; set; }
}
