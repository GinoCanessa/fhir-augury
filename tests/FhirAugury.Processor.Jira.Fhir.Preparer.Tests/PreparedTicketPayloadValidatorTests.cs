using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketPayloadValidatorTests
{
    [Fact]
    public void AcceptsValidRecommendationExisting()
    {
        PreparedTicketPayload payload = SamplePayload();
        payload.Recommendation = "existing";

        IReadOnlyList<string> errors = PreparedTicketPayloadValidator.Validate(payload);

        Assert.Empty(errors);
    }

    [Fact]
    public void RejectsUnknownRelatedJiraLinkType()
    {
        PreparedTicketPayload payload = SamplePayload();
        payload.RelatedJiraTickets[0].LinkType = "blocks";

        IReadOnlyList<string> errors = PreparedTicketPayloadValidator.Validate(payload);

        Assert.Contains(errors, error => error.Contains("LinkType", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsProposalCImpactIfPayloadAttemptsIt()
    {
        Assert.Null(typeof(PreparedTicketPayload).GetProperty("ProposalCImpact"));
    }

    private static PreparedTicketPayload SamplePayload() => new()
    {
        Key = "FHIR-123",
        RequestSummary = "request",
        ProposalA = "proposal a",
        ProposalAImpact = "Non-substantive",
        ProposalB = "proposal b",
        ProposalBImpact = "Compatible, substantive",
        ProposalC = "proposal c",
        Recommendation = "A",
        RecommendationJustification = "because",
        RelatedJiraTickets = [new PreparedTicketRelatedJiraPayload { AssociatedTicketKey = "FHIR-999", LinkType = "related" }],
    };
}
