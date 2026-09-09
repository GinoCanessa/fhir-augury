using System.Diagnostics;

namespace FhirAugury.Processing.Client;

internal static class AuthoringServiceBinding
{
    public const string Preparer = "Preparer";
    public const string Planner = "Planner";
    public const string BallotNotes = "BallotNotes";

    public static string Normalize(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (string.Equals(
                serviceName,
                Preparer,
                StringComparison.OrdinalIgnoreCase))
        {
            return Preparer;
        }
        if (string.Equals(
                serviceName,
                Planner,
                StringComparison.OrdinalIgnoreCase))
        {
            return Planner;
        }
        if (string.Equals(
                serviceName,
                BallotNotes,
                StringComparison.OrdinalIgnoreCase))
        {
            return BallotNotes;
        }

        throw new ArgumentException(
            $"Unknown authoring service '{serviceName}'. Expected {Preparer}, {Planner}, or {BallotNotes}.",
            nameof(serviceName));
    }

    public static string GetProcessorKind(string serviceName)
        => Normalize(serviceName) switch
        {
            Preparer or Planner => "jira-fhir",
            BallotNotes => "github-fhir-ballot-notes",
            _ => throw new UnreachableException(),
        };
}
