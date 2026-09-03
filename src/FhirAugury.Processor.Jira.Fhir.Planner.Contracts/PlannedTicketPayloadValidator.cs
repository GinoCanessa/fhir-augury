using System.Text.RegularExpressions;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;

public sealed partial class PlannedTicketPayloadValidator
{
    [GeneratedRegex(@"^[A-Z]+-\d+$", RegexOptions.Compiled)]
    private static partial Regex JiraKeyRegex();

    [GeneratedRegex(@"^[^/\s]+/[^/\s]+$", RegexOptions.Compiled)]
    private static partial Regex RepoKeyRegex();

    public static IReadOnlyList<string> Validate(PlannedTicketPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(payload.Key) || !JiraKeyRegex().IsMatch(payload.Key))
        {
            errors.Add("Key must be a valid Jira key.");
        }

        if (string.IsNullOrWhiteSpace(payload.ResolutionSummary))
        {
            errors.Add("ResolutionSummary is required.");
        }

        foreach (PlannedTicketRepoPayload repo in payload.Repos)
        {
            if (string.IsNullOrWhiteSpace(repo.RepoKey) || !RepoKeyRegex().IsMatch(repo.RepoKey))
            {
                errors.Add($"Repo '{repo.RepoKey}' is not a valid owner/name repo key.");
            }
        }

        foreach (PlannedTicketRepoChangePayload change in payload.RepoChanges)
        {
            if (string.IsNullOrWhiteSpace(change.TicketRepoId))
            {
                errors.Add("RepoChange.TicketRepoId is required.");
            }
            if (string.IsNullOrWhiteSpace(change.FilePath))
            {
                errors.Add("RepoChange.FilePath is required.");
            }
        }

        return errors;
    }

    public static void ThrowIfInvalid(PlannedTicketPayload payload)
    {
        IReadOnlyList<string> errors = Validate(payload);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(payload));
        }
    }
}
