namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

public static class PreparedTicketRecommendationValues
{
    public const string Existing = "existing";
    public const string ProposalA = "A";
    public const string ProposalB = "B";
    public const string ProposalC = "C";

    public static IReadOnlySet<string> Supported { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Existing,
        ProposalA,
        ProposalB,
        ProposalC,
    };
}
