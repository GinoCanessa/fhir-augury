using FhirAugury.Source.Confluence.Configuration;

namespace FhirAugury.Source.Confluence.Tests;

public class ConfluenceServiceOptionsTests
{
    [Fact]
    public void GetEffectiveSpaces_NullSpaces_ReturnsDefaults()
    {
        ConfluenceServiceOptions options = new();

        Assert.Equal(["FHIR", "FHIRI", "SOA"], options.GetEffectiveSpaces());
    }

    [Fact]
    public void GetEffectiveSpaces_EmptySpaces_ReturnsEmpty()
    {
        ConfluenceServiceOptions options = new() { Spaces = [] };

        Assert.Empty(options.GetEffectiveSpaces());
    }

    [Fact]
    public void GetEffectiveSpaces_CustomSpaces_ReturnsCustomList()
    {
        ConfluenceServiceOptions options = new() { Spaces = ["ABC", "DEF"] };

        Assert.Equal(["ABC", "DEF"], options.GetEffectiveSpaces());
    }
}
