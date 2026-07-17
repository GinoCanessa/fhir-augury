using FhirAugury.Common.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Configuration;

/// <summary>
/// Strongly typed configuration for the BallotNotes processor. Bound from the
/// <c>BallotNotes</c> section. The notes DB lives under <c>./cache/</c> because
/// it is co-consumed by the local <c>notes-site</c> renderer (mirroring
/// <c>ticket-site</c>'s <c>./cache/jira-preparer.db</c> default).
/// </summary>
public sealed class BallotNotesServiceOptions
{
    public const string SectionName = "BallotNotes";

    /// <summary>Canonical notes DB path, shared with the notes-site renderer.</summary>
    public string DatabasePath { get; set; } = "./cache/ballot-notes.db";

    public PortConfiguration Ports { get; set; } = new() { Http = 5174 };

    public BallotNotesHydrationOptions Hydration { get; set; } = new();

    /// <summary>Validates configuration. Returns human-readable errors; empty means valid.</summary>
    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            yield return "DatabasePath must be configured.";
        }

        if (Ports.Http <= 0)
        {
            yield return "Ports:Http must be a positive port number.";
        }

        foreach (string error in Hydration.Validate())
        {
            yield return error;
        }
    }
}
