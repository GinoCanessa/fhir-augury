using FhirAugury.Common.Text;

namespace FhirAugury.Orchestrator.CrossRef;

/// <summary>
/// Thin wrapper over <see cref="CrossRefPatterns"/> from Common.
/// Provides the orchestrator-specific entry point for cross-reference extraction.
/// </summary>
public static class CrossRefPatternHelper
{
    /// <summary>
    /// Extracts cross-reference links from a text string using the shared patterns.
    /// </summary>
    public static List<(string TargetType, string TargetId, string Context)> ExtractLinks(string text)
        => CrossRefPatterns.ExtractLinks(text);
}
