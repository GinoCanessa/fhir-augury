using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Configuration;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

public sealed class BallotNotesServiceOptionsTests
{
    [Fact]
    public void DefaultsStageAuthoringWithoutActivationSwitch()
    {
        BallotNotesServiceOptions options = new();

        Assert.Equal("./cache/ballot-notes.db", options.DatabasePath);
        Assert.Equal(4, options.MaxConcurrentProcessingThreads);
        Assert.Equal(
            "./cache/snapshots/ballot-notes",
            options.SnapshotDirectory);
        Assert.Contains("{noteId}", options.ArtifactAuthoringCommand);
        Assert.Contains("{noteId}", options.PageAuthoringCommand);
        Assert.Contains("{noteId}", options.DataTypeAuthoringCommand);
        Assert.DoesNotContain(
            options.GetType().GetProperties(),
            property => property.Name.Contains(
                "Activate",
                StringComparison.OrdinalIgnoreCase));
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void ValidationRejectsIncompleteStagedCommands()
    {
        BallotNotesServiceOptions options = new()
        {
            ArtifactAuthoringCommand = "copilot",
            PageAuthoringCommand = "",
            DataTypeAuthoringCommand = "copilot {noteId}",
        };

        string[] errors = options.Validate().ToArray();

        Assert.Contains(
            errors,
            error => error.Contains(
                nameof(options.ArtifactAuthoringCommand),
                StringComparison.Ordinal));
        Assert.Contains(
            errors,
            error => error.Contains(
                nameof(options.PageAuthoringCommand),
                StringComparison.Ordinal));
    }
}
