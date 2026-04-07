using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Ingestion;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.GitHub.Tests.Ingestion;

public class TagWeightResolverTests
{
    [Fact]
    public void ResolveWeight_DefaultConfig_ReturnsOne()
    {
        TagWeightResolver resolver = CreateResolver(new TagWeightOptions());

        double weight = resolver.ResolveWeight("resource", "Patient", null);

        Assert.Equal(1.0, weight);
    }

    [Fact]
    public void ResolveWeight_CategoryWeight_Applied()
    {
        TagWeightResolver resolver = CreateResolver(new TagWeightOptions
        {
            CategoryWeights = new() { ["resource"] = 1.5 },
        });

        double weight = resolver.ResolveWeight("resource", "Patient", null);

        Assert.Equal(1.5, weight);
    }

    [Fact]
    public void ResolveWeight_ModifierMultiplier_Applied()
    {
        TagWeightResolver resolver = CreateResolver(new TagWeightOptions
        {
            CategoryWeights = new() { ["resource"] = 1.0 },
            ModifierMultipliers = new() { ["removed"] = 0.3 },
        });

        double weight = resolver.ResolveWeight("resource", "Animal", "removed");

        Assert.Equal(0.3, weight, 5);
    }

    [Fact]
    public void ResolveWeight_NameOverride_TakesPriority()
    {
        TagWeightResolver resolver = CreateResolver(new TagWeightOptions
        {
            CategoryWeights = new() { ["resource"] = 1.0 },
            NameOverrides = new() { ["Patient"] = 2.0 },
        });

        double weight = resolver.ResolveWeight("resource", "Patient", null);

        Assert.Equal(2.0, weight);
    }

    [Fact]
    public void ResolveWeight_NameOverrideWithModifier_BothApplied()
    {
        TagWeightResolver resolver = CreateResolver(new TagWeightOptions
        {
            NameOverrides = new() { ["Patient"] = 2.0 },
            ModifierMultipliers = new() { ["draft"] = 0.7 },
        });

        double weight = resolver.ResolveWeight("resource", "Patient", "draft");

        Assert.Equal(1.4, weight, 5);
    }

    [Fact]
    public void ResolveWeight_UnknownCategory_FallsBackToDefault()
    {
        TagWeightResolver resolver = CreateResolver(new TagWeightOptions
        {
            Default = 0.5,
            CategoryWeights = new() { ["resource"] = 1.0 },
        });

        double weight = resolver.ResolveWeight("unknown-category", "SomeName", null);

        Assert.Equal(0.5, weight);
    }

    [Fact]
    public void ResolveWeight_UnknownModifier_NoMultiplier()
    {
        TagWeightResolver resolver = CreateResolver(new TagWeightOptions
        {
            CategoryWeights = new() { ["resource"] = 1.0 },
            ModifierMultipliers = new() { ["removed"] = 0.3 },
        });

        double weight = resolver.ResolveWeight("resource", "Patient", "unknown-modifier");

        Assert.Equal(1.0, weight);
    }

    private static TagWeightResolver CreateResolver(TagWeightOptions options)
    {
        return new TagWeightResolver(Options.Create(options));
    }
}
