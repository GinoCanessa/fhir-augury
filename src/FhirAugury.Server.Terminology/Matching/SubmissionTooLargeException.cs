namespace FhirAugury.Server.Terminology.Matching;

/// <summary>
/// Thrown when a submitted CodeSystem/ValueSet flattens to more
/// concepts than <c>TerminologyServiceOptions.MaxSubmissionConcepts</c>.
/// </summary>
public sealed class SubmissionTooLargeException : Exception
{
    public int Cap { get; }
    public int Submitted { get; }

    public SubmissionTooLargeException(int cap, int submitted)
        : base($"Submission flattens to {submitted} concepts; cap is {cap}.")
    {
        Cap = cap;
        Submitted = submitted;
    }
}
