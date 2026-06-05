using System.Text.RegularExpressions;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;

public sealed partial class PlannedTicketTopicGroupingPayloadValidator
{
    [GeneratedRegex(@"^[A-Z]+-\d+$", RegexOptions.Compiled)]
    private static partial Regex JiraKeyRegex();

    [GeneratedRegex(@"^[^/\s]+/[^/\s]+$", RegexOptions.Compiled)]
    private static partial Regex RepoKeyRegex();

    public static IReadOnlyList<string> Validate(PlannedTicketTopicGroupingPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(payload.WorkGroupClean))
        {
            errors.Add("WorkGroupClean is required.");
        }
        if (string.IsNullOrWhiteSpace(payload.Specification))
        {
            errors.Add("Specification is required.");
        }
        if (string.IsNullOrWhiteSpace(payload.Type))
        {
            errors.Add("Type is required.");
        }

        foreach (PlannedTicketTopicPayload topic in payload.Topics)
        {
            if (string.IsNullOrWhiteSpace(topic.ShortDescription))
            {
                errors.Add("Topic.ShortDescription is required.");
            }

            HashSet<string> seenRepos = new(StringComparer.OrdinalIgnoreCase);
            foreach (string repo in topic.SpannedRepos)
            {
                if (string.IsNullOrWhiteSpace(repo) || !RepoKeyRegex().IsMatch(repo))
                {
                    errors.Add($"SpannedRepos entry '{repo}' is not a valid owner/name repo key.");
                    continue;
                }
                // Case-insensitive duplicates are silently de-duplicated by
                // NormalizeSpannedRepos at persist time, so they are not
                // a validation error.
                seenRepos.Add(repo);
            }

            foreach (PlannedTicketTopicGroupPayload group in topic.LinkedTicketGroups)
            {
                if (string.IsNullOrWhiteSpace(group.FirstTicketKey) || !JiraKeyRegex().IsMatch(group.FirstTicketKey))
                {
                    errors.Add("LinkedTicketGroup.FirstTicketKey must be a valid Jira key.");
                }

                foreach (PlannedTicketTopicGroupMemberPayload member in group.Members)
                {
                    if (string.IsNullOrWhiteSpace(member.TicketKey) || !JiraKeyRegex().IsMatch(member.TicketKey))
                    {
                        errors.Add("LinkedTicketGroup.Member.TicketKey must be a valid Jira key.");
                    }
                }
            }

            foreach (string remaining in topic.RemainingTicketKeys)
            {
                if (string.IsNullOrWhiteSpace(remaining) || !JiraKeyRegex().IsMatch(remaining))
                {
                    errors.Add("RemainingTicketKeys entry must be a valid Jira key.");
                }
            }
        }

        return errors;
    }

    public static void ThrowIfInvalid(PlannedTicketTopicGroupingPayload payload)
    {
        IReadOnlyList<string> errors = Validate(payload);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(payload));
        }
    }

    public static IReadOnlyList<string> NormalizeSpannedRepos(IEnumerable<string> input)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> result = [];
        foreach (string repo in input)
        {
            if (string.IsNullOrWhiteSpace(repo))
            {
                continue;
            }
            if (seen.Add(repo))
            {
                result.Add(repo);
            }
        }
        return result;
    }
}
