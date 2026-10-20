using FhirAugury.Common.Configuration;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Configuration;

/// <summary>
/// Strongly typed configuration for the BallotNotes processor. Bound from the
/// <c>BallotNotes</c> section. The processor owns the live notes database and
/// publishes sanitized immutable snapshots for review-site generation.
/// </summary>
public sealed class BallotNotesServiceOptions : ProcessingServiceOptions
{
    public new const string SectionName = "BallotNotes";

    public BallotNotesServiceOptions()
    {
        DatabasePath = "./cache/ballot-notes.db";
        SyncSchedule = "00:00:01";
        MaxConcurrentProcessingThreads = 4;
        StartProcessingOnStartup = true;
        Ports = new PortConfiguration { Http = 5174 };
        SnapshotDirectory = "./cache/snapshots/ballot-notes";
    }

    public BallotNotesHydrationOptions Hydration { get; set; } = new();
    public string ArtifactAuthoringCommand { get; set; } =
        "copilot -p \"/notes-artifact {noteId}\" --allow-all";
    public string PageAuthoringCommand { get; set; } =
        "copilot -p \"/notes-page {noteId}\" --allow-all";
    public string DataTypeAuthoringCommand { get; set; } =
        "copilot -p \"/notes-datatype {noteId}\" --allow-all";
    public string? AuthoringCallbackAddress { get; set; }

    /// <summary>Validates configuration. Returns human-readable errors; empty means valid.</summary>
    public new IEnumerable<string> Validate()
    {
        foreach (string error in base.Validate())
        {
            yield return error;
        }
        foreach ((string name, string command) in new[]
        {
            (nameof(ArtifactAuthoringCommand), ArtifactAuthoringCommand),
            (nameof(PageAuthoringCommand), PageAuthoringCommand),
            (nameof(DataTypeAuthoringCommand), DataTypeAuthoringCommand),
        })
        {
            if (string.IsNullOrWhiteSpace(command) ||
                !command.Contains(
                    "{noteId}",
                    StringComparison.OrdinalIgnoreCase))
            {
                yield return $"{name} must be non-empty and contain the {{noteId}} token.";
            }
            if (command.Contains(
                    "{dbPath}",
                    StringComparison.OrdinalIgnoreCase) ||
                command.Contains(
                    "{operationToken}",
                    StringComparison.OrdinalIgnoreCase))
            {
                yield return $"{name} must use callback context from the worker environment.";
            }
        }

        foreach (string error in Hydration.Validate())
        {
            yield return error;
        }
    }
}
