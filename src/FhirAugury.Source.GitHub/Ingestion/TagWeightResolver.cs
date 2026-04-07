using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Configuration;

/// <summary>
/// Resolves tag weights from configuration, applying category weights,
/// modifier multipliers, and name overrides.
/// </summary>
public class TagWeightResolver(IOptions<TagWeightOptions> options)
{
    /// <summary>
    /// Calculates the weight for a tag based on its category, name, and modifier.
    /// Name overrides take priority over category weights. Modifier multipliers
    /// are applied on top.
    /// </summary>
    public double ResolveWeight(string category, string name, string? modifier)
    {
        TagWeightOptions opts = options.Value;

        // Name override takes priority over category weight
        double baseWeight = opts.NameOverrides.GetValueOrDefault(name,
            opts.CategoryWeights.GetValueOrDefault(category, opts.Default));

        // Apply modifier multiplier
        if (modifier is not null &&
            opts.ModifierMultipliers.TryGetValue(modifier, out double multiplier))
        {
            baseWeight *= multiplier;
        }

        return baseWeight;
    }
}
