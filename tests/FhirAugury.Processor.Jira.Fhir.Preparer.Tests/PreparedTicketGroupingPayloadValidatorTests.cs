using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketGroupingPayloadValidatorTests
{
    [Fact]
    public void Validate_AcceptsHappyPath()
    {
        PreparedTicketGroupingPayload payload = ValidPayload();
        IReadOnlyList<string> errors = PreparedTicketGroupingPayloadValidator.Validate(payload);
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_RejectsBadWorkGroupClean()
    {
        PreparedTicketGroupingPayload payload = ValidPayload();
        payload.WorkGroupClean = "9 Bad Name";
        IReadOnlyList<string> errors = PreparedTicketGroupingPayloadValidator.Validate(payload);
        Assert.Contains(errors, e => e.Contains("WorkGroupClean", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsEmptyTopic()
    {
        PreparedTicketGroupingPayload payload = ValidPayload();
        payload.Topics[0].LinkedTicketGroups.Clear();
        payload.Topics[0].RemainingTicketKeys.Clear();
        IReadOnlyList<string> errors = PreparedTicketGroupingPayloadValidator.Validate(payload);
        Assert.Contains(errors, e => e.Contains("at least one member", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsSingletonTopic()
    {
        PreparedTicketGroupingPayload payload = ValidPayload();
        payload.Topics[0].LinkedTicketGroups.Clear();
        payload.Topics[0].RemainingTicketKeys.Clear();
        payload.Topics[0].RemainingTicketKeys.Add("FHIR-77");
        IReadOnlyList<string> errors = PreparedTicketGroupingPayloadValidator.Validate(payload);
        Assert.Contains(errors, e => e.Contains("at least two tickets", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsLinkedGroupOfSizeOne()
    {
        PreparedTicketGroupingPayload payload = ValidPayload();
        PreparedTicketTopicGroupPayload group = payload.Topics[0].LinkedTicketGroups[0];
        group.Members.RemoveAt(1);
        IReadOnlyList<string> errors = PreparedTicketGroupingPayloadValidator.Validate(payload);
        Assert.Contains(errors, e => e.Contains("at least two tickets", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsDuplicateMemberAcrossTopics()
    {
        PreparedTicketGroupingPayload payload = ValidPayload();
        payload.Topics.Add(new PreparedTicketTopicPayload
        {
            ShortDescription = "Other topic",
            LongerDescription = "Other longer.",
            RemainingTicketKeys = ["FHIR-1", "FHIR-50"],
        });
        IReadOnlyList<string> errors = PreparedTicketGroupingPayloadValidator.Validate(payload);
        Assert.Contains(errors, e => e.Contains("more than one Topic", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsBadTicketKey()
    {
        PreparedTicketGroupingPayload payload = ValidPayload();
        payload.Topics[0].RemainingTicketKeys = ["not-a-key"];
        IReadOnlyList<string> errors = PreparedTicketGroupingPayloadValidator.Validate(payload);
        Assert.Contains(errors, e => e.Contains("valid Jira key", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsBadRationaleMarkdown()
    {
        PreparedTicketGroupingPayload payload = ValidPayload();
        payload.Topics[0].LinkedTicketGroups[0].Rationale = "# heading not allowed";
        IReadOnlyList<string> errors = PreparedTicketGroupingPayloadValidator.Validate(payload);
        Assert.Contains(errors, e => e.Contains("Rationale", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsMissingFirstTicketKeyInOwnMembers()
    {
        PreparedTicketGroupingPayload payload = ValidPayload();
        PreparedTicketTopicGroupPayload group = payload.Topics[0].LinkedTicketGroups[0];
        group.FirstTicketKey = "FHIR-99";
        IReadOnlyList<string> errors = PreparedTicketGroupingPayloadValidator.Validate(payload);
        Assert.Contains(errors, e => e.Contains("must appear in its own Members", StringComparison.Ordinal));
    }

    private static PreparedTicketGroupingPayload ValidPayload() => new()
    {
        WorkGroupClean = "OrdersAndObservations",
        WorkGroupDisplay = "Orders and Observations",
        Specification = "FHIR Core",
        Type = "Change Request",
        Topics =
        [
            new PreparedTicketTopicPayload
            {
                ShortDescription = "Observation polymorphic value",
                LongerDescription = "Covers ticket fan-out around Observation.value.",
                RenderOrderHint = 0,
                LinkedTicketGroups =
                [
                    new PreparedTicketTopicGroupPayload
                    {
                        FirstTicketKey = "FHIR-1",
                        Rationale = "Both edit `Observation.value[x]` in compatible ways.",
                        Members =
                        [
                            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-1", Order = 0 },
                            new PreparedTicketTopicGroupMemberPayload { TicketKey = "FHIR-2", Order = 1 },
                        ],
                    },
                ],
                RemainingTicketKeys = ["FHIR-50"],
            },
        ],
    };
}
