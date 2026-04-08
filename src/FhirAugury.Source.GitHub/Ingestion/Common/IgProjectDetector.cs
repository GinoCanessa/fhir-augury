namespace FhirAugury.Source.GitHub.Ingestion.Common;

/// <summary>
/// Detects IG Publisher project markers in a repository directory.
/// Used by Incubator and IG strategies for validation.
/// </summary>
public static class IgProjectDetector
{
    /// <summary>Detection result with individual marker flags.</summary>
    public record DetectionResult(
        bool HasSushiConfig,
        bool HasIgIni,
        bool HasInputDir,
        bool HasFshDir)
    {
        /// <summary>True if any IG project marker was detected.</summary>
        public bool IsIgProject => HasSushiConfig || HasIgIni;
    }

    /// <summary>
    /// Scans the given directory for IG Publisher project markers.
    /// </summary>
    public static DetectionResult Detect(string clonePath)
    {
        bool hasSushiConfig = File.Exists(Path.Combine(clonePath, "sushi-config.yaml"));
        bool hasIgIni = File.Exists(Path.Combine(clonePath, "ig.ini"));
        bool hasInputDir = Directory.Exists(Path.Combine(clonePath, "input"));
        bool hasFshDir = Directory.Exists(Path.Combine(clonePath, "fsh")) ||
                         Directory.Exists(Path.Combine(clonePath, "input", "fsh"));

        return new DetectionResult(hasSushiConfig, hasIgIni, hasInputDir, hasFshDir);
    }
}
