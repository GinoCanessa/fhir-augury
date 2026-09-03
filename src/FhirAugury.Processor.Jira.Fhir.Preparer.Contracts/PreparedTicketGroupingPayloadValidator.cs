using System.Text.RegularExpressions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

public sealed partial class PreparedTicketGroupingPayloadValidator
{
    public static IReadOnlyList<string> Validate(PreparedTicketGroupingPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(payload.WorkGroupClean) || !WorkGroupCleanRegex().IsMatch(payload.WorkGroupClean))
        {
            errors.Add("WorkGroupClean must match the nameClean convention (letters/digits, leading letter).");
        }

        if (string.IsNullOrWhiteSpace(payload.WorkGroupDisplay))
        {
            errors.Add("WorkGroupDisplay must be non-empty.");
        }

        if (string.IsNullOrWhiteSpace(payload.Specification))
        {
            errors.Add("Specification must be non-empty (use 'Unspecified' verbatim when absent).");
        }

        if (string.IsNullOrWhiteSpace(payload.Type))
        {
            errors.Add("Type must be non-empty.");
        }

        HashSet<string> seenAcrossTopics = new(StringComparer.Ordinal);
        for (int topicIndex = 0; topicIndex < payload.Topics.Count; topicIndex++)
        {
            PreparedTicketTopicPayload topic = payload.Topics[topicIndex];
            string topicLabel = $"Topics[{topicIndex}]";

            if (string.IsNullOrWhiteSpace(topic.ShortDescription))
            {
                errors.Add($"{topicLabel}.ShortDescription must be non-empty.");
            }
            else if (!RationaleMarkdownValidator.IsValid(topic.ShortDescription, out string? shortReason))
            {
                errors.Add($"{topicLabel}.ShortDescription invalid: {shortReason}");
            }

            if (string.IsNullOrWhiteSpace(topic.LongerDescription))
            {
                errors.Add($"{topicLabel}.LongerDescription must be non-empty.");
            }
            else if (!RationaleMarkdownValidator.IsValid(topic.LongerDescription, out string? longerReason))
            {
                errors.Add($"{topicLabel}.LongerDescription invalid: {longerReason}");
            }

            if (topic.RenderOrderHint is < 0)
            {
                errors.Add($"{topicLabel}.RenderOrderHint must be >= 0 when supplied.");
            }

            int memberTotal = 0;
            for (int groupIndex = 0; groupIndex < topic.LinkedTicketGroups.Count; groupIndex++)
            {
                PreparedTicketTopicGroupPayload group = topic.LinkedTicketGroups[groupIndex];
                string groupLabel = $"{topicLabel}.LinkedTicketGroups[{groupIndex}]";

                if (string.IsNullOrWhiteSpace(group.FirstTicketKey) || !JiraKeyRegex().IsMatch(group.FirstTicketKey))
                {
                    errors.Add($"{groupLabel}.FirstTicketKey must be a valid Jira key.");
                }

                if (string.IsNullOrWhiteSpace(group.Rationale))
                {
                    errors.Add($"{groupLabel}.Rationale must be non-empty.");
                }
                else if (!RationaleMarkdownValidator.IsValid(group.Rationale, out string? rationaleReason))
                {
                    errors.Add($"{groupLabel}.Rationale invalid: {rationaleReason}");
                }

                if (group.Members.Count < 2)
                {
                    errors.Add($"{groupLabel}.Members must contain at least two tickets (size-1 groups are not linked groups).");
                }

                bool firstTicketInMembers = false;
                HashSet<int> seenOrders = [];
                for (int memberIndex = 0; memberIndex < group.Members.Count; memberIndex++)
                {
                    PreparedTicketTopicGroupMemberPayload member = group.Members[memberIndex];
                    string memberLabel = $"{groupLabel}.Members[{memberIndex}]";

                    if (string.IsNullOrWhiteSpace(member.TicketKey) || !JiraKeyRegex().IsMatch(member.TicketKey))
                    {
                        errors.Add($"{memberLabel}.TicketKey must be a valid Jira key.");
                        continue;
                    }

                    if (member.Order < 0)
                    {
                        errors.Add($"{memberLabel}.Order must be >= 0.");
                    }

                    if (!seenOrders.Add(member.Order))
                    {
                        errors.Add($"{memberLabel}.Order duplicates another member in the same group.");
                    }

                    if (!seenAcrossTopics.Add(member.TicketKey))
                    {
                        errors.Add($"{memberLabel}.TicketKey '{member.TicketKey}' appears in more than one Topic in this partition.");
                    }

                    if (string.Equals(member.TicketKey, group.FirstTicketKey, StringComparison.Ordinal))
                    {
                        firstTicketInMembers = true;
                    }

                    memberTotal++;
                }

                if (!firstTicketInMembers && !string.IsNullOrWhiteSpace(group.FirstTicketKey))
                {
                    errors.Add($"{groupLabel}.FirstTicketKey '{group.FirstTicketKey}' must appear in its own Members list.");
                }
            }

            for (int remainingIndex = 0; remainingIndex < topic.RemainingTicketKeys.Count; remainingIndex++)
            {
                string remaining = topic.RemainingTicketKeys[remainingIndex];
                string remainingLabel = $"{topicLabel}.RemainingTicketKeys[{remainingIndex}]";
                if (string.IsNullOrWhiteSpace(remaining) || !JiraKeyRegex().IsMatch(remaining))
                {
                    errors.Add($"{remainingLabel} must be a valid Jira key.");
                    continue;
                }

                if (!seenAcrossTopics.Add(remaining))
                {
                    errors.Add($"{remainingLabel} '{remaining}' appears in more than one Topic in this partition.");
                }

                memberTotal++;
            }

            if (memberTotal == 0)
            {
                errors.Add($"{topicLabel} must contain at least one member (an empty Topic is not renderable).");
            }
            else if (memberTotal < 2)
            {
                errors.Add($"{topicLabel} must contain at least two tickets (singletons are implicit; see source resolution).");
            }
        }

        return errors;
    }

    public static void ThrowIfInvalid(PreparedTicketGroupingPayload payload)
    {
        IReadOnlyList<string> errors = Validate(payload);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(payload));
        }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]+$")]
    private static partial Regex WorkGroupCleanRegex();

    [GeneratedRegex("^[A-Z][A-Z0-9]+-\\d+$")]
    private static partial Regex JiraKeyRegex();
}
