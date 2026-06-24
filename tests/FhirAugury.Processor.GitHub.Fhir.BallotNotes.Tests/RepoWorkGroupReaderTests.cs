using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

/// <summary>
/// Exercises <see cref="RepoWorkGroupReader"/>'s page "Responsible Owner" marker
/// extraction over a temp clone.
/// </summary>
public sealed class RepoWorkGroupReaderTests : IDisposable
{
    private readonly string _tempDir;

    public RepoWorkGroupReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "repowg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_tempDir, "source"));
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    [Fact]
    public void ReadPageMarker_extracts_code()
    {
        File.WriteAllText(
            Path.Combine(_tempDir, "source", "patient.html"),
            "<html><body><td id=\"wg\"><a href=\"[%wg pa%]\">[%wgt pa%]</a> Work Group</td></body></html>");

        Assert.Equal("pa", RepoWorkGroupReader.ReadPageMarker(_tempDir, "patient"));
    }

    [Fact]
    public void ReadPageMarker_returns_null_when_no_marker()
    {
        File.WriteAllText(Path.Combine(_tempDir, "source", "plain.html"), "<html><body>no marker here</body></html>");

        Assert.Null(RepoWorkGroupReader.ReadPageMarker(_tempDir, "plain"));
    }

    [Fact]
    public void ReadPageMarker_returns_null_when_file_absent()
        => Assert.Null(RepoWorkGroupReader.ReadPageMarker(_tempDir, "missing"));
}
