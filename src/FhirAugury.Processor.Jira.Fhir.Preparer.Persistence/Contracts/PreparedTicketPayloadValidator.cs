using System.Text.RegularExpressions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

public sealed partial class PreparedTicketPayloadValidator
{
    private static readonly HashSet<string> ValidImpacts = new(StringComparer.Ordinal)
    {
        "Non-substantive",
        "Compatible, substantive",
        "Non-compatible",
    };

    private static readonly HashSet<string> ValidRecommendations = new(StringComparer.Ordinal)
    {
        "existing",
        "A",
        "B",
        "C",
    };

    private static readonly HashSet<string> ValidLinkTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "linked",
        "related",
    };

    public static IReadOnlyList<string> Validate(PreparedTicketPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(payload.Key) || !JiraKeyRegex().IsMatch(payload.Key))
        {
            errors.Add("Key must be a valid Jira key.");
        }

        Require(payload.RequestSummary, nameof(payload.RequestSummary), errors);
        Require(payload.ProposalA, nameof(payload.ProposalA), errors);
        Require(payload.ProposalB, nameof(payload.ProposalB), errors);
        Require(payload.ProposalC, nameof(payload.ProposalC), errors);
        Require(payload.RecommendationJustification, nameof(payload.RecommendationJustification), errors);
        if (!ValidImpacts.Contains(payload.ProposalAImpact))
        {
            errors.Add("ProposalAImpact is not supported.");
        }

        if (!ValidImpacts.Contains(payload.ProposalBImpact))
        {
            errors.Add("ProposalBImpact is not supported.");
        }

        if (!ValidRecommendations.Contains(payload.Recommendation))
        {
            errors.Add("Recommendation is not supported.");
        }

        foreach (PreparedTicketRelatedJiraPayload related in payload.RelatedJiraTickets)
        {
            if (string.IsNullOrWhiteSpace(related.AssociatedTicketKey) || !JiraKeyRegex().IsMatch(related.AssociatedTicketKey))
            {
                errors.Add("Related Jira ticket key must be valid.");
            }

            if (!ValidLinkTypes.Contains(related.LinkType))
            {
                errors.Add("Related Jira LinkType must be linked or related.");
            }
        }

        return errors;
    }

    public static void ThrowIfInvalid(PreparedTicketPayload payload)
    {
        IReadOnlyList<string> errors = Validate(payload);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(payload));
        }
    }

    private static void Require(string? value, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} must be non-empty.");
        }
    }

    [GeneratedRegex("^[A-Z][A-Z0-9]+-\\d+$")]
    private static partial Regex JiraKeyRegex();
}
