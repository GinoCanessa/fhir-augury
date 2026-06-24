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

    [Fact]
    public void ReadArtifactWg_extracts_code()
    {
        string rel = WriteSd(
            "observation",
            "structuredefinition-Observation.xml",
            workGroup: "oo",
            baseDefinition: null);

        string? code = RepoWorkGroupReader.ReadArtifactWg(_tempDir, [Sd(rel)]);

        Assert.Equal("oo", code);
    }

    [Fact]
    public void ReadArtifactWg_returns_null_when_no_wg_extension()
    {
        string rel = WriteSd("foo", "structuredefinition-Foo.xml", workGroup: null, baseDefinition: null);

        Assert.Null(RepoWorkGroupReader.ReadArtifactWg(_tempDir, [Sd(rel)]));
    }

    [Fact]
    public void ReadBaseResourceName_extracts_last_segment()
    {
        string rel = WriteSd(
            "myprofile",
            "structuredefinition-MyProfile.xml",
            workGroup: null,
            baseDefinition: "http://hl7.org/fhir/StructureDefinition/Patient");

        Assert.Equal("Patient", RepoWorkGroupReader.ReadBaseResourceName(_tempDir, [Sd(rel)]));
    }

    private string WriteSd(string folder, string fileName, string? workGroup, string? baseDefinition)
    {
        string dir = Path.Combine(_tempDir, "source", folder);
        Directory.CreateDirectory(dir);

        string wgXml = workGroup is null
            ? string.Empty
            : $"<extension url=\"http://hl7.org/fhir/StructureDefinition/structuredefinition-wg\"><valueCode value=\"{workGroup}\"/></extension>";
        string baseXml = baseDefinition is null ? string.Empty : $"<baseDefinition value=\"{baseDefinition}\"/>";
        string name = Path.GetFileNameWithoutExtension(fileName).Replace("structuredefinition-", string.Empty);

        string xml =
            "<StructureDefinition xmlns=\"http://hl7.org/fhir\">" +
            $"<url value=\"http://hl7.org/fhir/StructureDefinition/{name}\"/>" +
            $"<name value=\"{name}\"/>" +
            "<status value=\"active\"/>" +
            "<kind value=\"resource\"/>" +
            "<abstract value=\"false\"/>" +
            $"<type value=\"{name}\"/>" +
            wgXml +
            baseXml +
            "</StructureDefinition>";

        string fullPath = Path.Combine(dir, fileName);
        File.WriteAllText(fullPath, xml);
        return Path.GetRelativePath(_tempDir, fullPath).Replace('\\', '/');
    }

    private static ResolvedSourceFile Sd(string path) => new() { Path = path, Role = "StructureDefinition" };
}
