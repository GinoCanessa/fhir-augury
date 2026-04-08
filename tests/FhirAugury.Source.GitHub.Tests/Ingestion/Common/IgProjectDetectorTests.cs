using FhirAugury.Source.GitHub.Ingestion.Common;

namespace FhirAugury.Source.GitHub.Tests.Ingestion.Common;

public class IgProjectDetectorTests : IDisposable
{
    private readonly string _tempDir;

    public IgProjectDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ig-detect-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Detect_EmptyDirectory_NoMarkers()
    {
        IgProjectDetector.DetectionResult result = IgProjectDetector.Detect(_tempDir);

        Assert.False(result.HasSushiConfig);
        Assert.False(result.HasIgIni);
        Assert.False(result.HasInputDir);
        Assert.False(result.HasFshDir);
        Assert.False(result.IsIgProject);
    }

    [Fact]
    public void Detect_SushiConfig_IsIgProject()
    {
        File.WriteAllText(Path.Combine(_tempDir, "sushi-config.yaml"), "id: test");

        IgProjectDetector.DetectionResult result = IgProjectDetector.Detect(_tempDir);

        Assert.True(result.HasSushiConfig);
        Assert.True(result.IsIgProject);
    }

    [Fact]
    public void Detect_IgIni_IsIgProject()
    {
        File.WriteAllText(Path.Combine(_tempDir, "ig.ini"), "[ig]\ntemplate=fhir.base.template");

        IgProjectDetector.DetectionResult result = IgProjectDetector.Detect(_tempDir);

        Assert.True(result.HasIgIni);
        Assert.True(result.IsIgProject);
    }

    [Fact]
    public void Detect_InputDir_ButNotIgProject()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "input"));

        IgProjectDetector.DetectionResult result = IgProjectDetector.Detect(_tempDir);

        Assert.True(result.HasInputDir);
        Assert.False(result.IsIgProject);
    }

    [Fact]
    public void Detect_FshDir_AtRoot()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "fsh"));

        IgProjectDetector.DetectionResult result = IgProjectDetector.Detect(_tempDir);

        Assert.True(result.HasFshDir);
    }

    [Fact]
    public void Detect_FshDir_UnderInput()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "input", "fsh"));

        IgProjectDetector.DetectionResult result = IgProjectDetector.Detect(_tempDir);

        Assert.True(result.HasFshDir);
    }

    [Fact]
    public void Detect_AllMarkers()
    {
        File.WriteAllText(Path.Combine(_tempDir, "sushi-config.yaml"), "id: test");
        File.WriteAllText(Path.Combine(_tempDir, "ig.ini"), "[ig]");
        Directory.CreateDirectory(Path.Combine(_tempDir, "input"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "input", "fsh"));

        IgProjectDetector.DetectionResult result = IgProjectDetector.Detect(_tempDir);

        Assert.True(result.HasSushiConfig);
        Assert.True(result.HasIgIni);
        Assert.True(result.HasInputDir);
        Assert.True(result.HasFshDir);
        Assert.True(result.IsIgProject);
    }
}
