namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;

public static class PreparedTicketImpactValues
{
    public const string NonSubstantive = "Non-substantive";
    public const string CompatibleSubstantive = "Compatible, substantive";
    public const string NonCompatible = "Non-compatible";
    public const string NotAssessed = "Not assessed";

    public static IReadOnlySet<string> Supported { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        NonSubstantive,
        CompatibleSubstantive,
        NonCompatible,
        NotAssessed,
    };
}
