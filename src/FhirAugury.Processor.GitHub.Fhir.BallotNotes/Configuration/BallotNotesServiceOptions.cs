using FhirAugury.Common.Configuration;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Configuration;

/// <summary>
/// Strongly typed configuration for the BallotNotes processor. Bound from the
/// <c>BallotNotes</c> section. The notes DB lives under <c>./cache/</c> because
/// it is co-consumed by the local <c>notes-site</c> renderer (mirroring
/// <c>ticket-site</c>'s <c>./cache/jira-preparer.db</c> default).
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
        "copilot --allow-all-tools -p \"/notes-artifact {noteId}\"";
    public string PageAuthoringCommand { get; set; } =
        "copilot --allow-all-tools -p \"/notes-page {noteId}\"";
    public string DataTypeAuthoringCommand { get; set; } =
        "copilot --allow-all-tools -p \"/notes-datatype {noteId}\"";
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
                !command.Contains("{noteId}", StringComparison.Ordinal))
            {
                yield return $"{name} must be non-empty and contain the {{noteId}} token.";
            }
        }

        foreach (string error in Hydration.Validate())
        {
            yield return error;
        }
    }
}
