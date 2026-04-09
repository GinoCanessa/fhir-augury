namespace FhirAugury.Source.GitHub.Configuration;

/// <summary>
/// Configurable weights for file tags, allowing categories, modifiers,
/// and specific names to influence search ranking.
/// </summary>
public class TagWeightOptions
{
    /// <summary>Weight by tag category (e.g., "resource" → 1.0).</summary>
    public Dictionary<string, double> CategoryWeights { get; set; } = [];

    /// <summary>Multiplier by modifier (e.g., "removed" → 0.3, "draft" → 0.7).</summary>
    public Dictionary<string, double> ModifierMultipliers { get; set; } = [];

    /// <summary>Override weight for specific tag names (e.g., "Patient" → 1.2).</summary>
    public Dictionary<string, double> NameOverrides { get; set; } = [];

    /// <summary>Default weight when no specific rule matches.</summary>
    public double Default { get; set; } = 1.0;
}
