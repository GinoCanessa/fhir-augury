using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

public sealed class DatatypePageMapTests
{
    [Theory]
    [InlineData("Quantity", "quantity")]
    [InlineData("Period", "period")]
    [InlineData("Dosage", "dosage")]
    [InlineData("Reference", "references")]
    [InlineData("ContactDetail", "metadatatypes")]
    [InlineData("DataRequirement", "metadatatypes")]
    [InlineData("UsageContext", "metadatatypes")]
    [InlineData("Contributor", "metadatatypes")]
    public void ResolveStem_maps_overrides_and_default(string datatype, string expected)
        => Assert.Equal(expected, DatatypePageMap.ResolveStem(datatype));

    [Fact]
    public void ResolveStem_is_case_insensitive_for_overrides()
        => Assert.Equal("references", DatatypePageMap.ResolveStem("reference"));

    [Fact]
    public void ComputeOwnedPages_keeps_only_pages_existing_at_head()
    {
        HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase)
        {
            "source/dosage.html",
            "source/metadatatypes.html",
            // note: no source/quantity.html — Quantity has no own-page
        };

        IReadOnlySet<string> owned = DatatypePageMap.ComputeOwnedPages(
            ["Dosage", "Quantity", "ContactDetail"],
            existing.Contains);

        Assert.Contains("source/dosage.html", owned);
        Assert.Contains("source/metadatatypes.html", owned);
        Assert.DoesNotContain("source/quantity.html", owned);
    }

    [Fact]
    public void ComputeOwnedPages_skips_blank_names()
    {
        IReadOnlySet<string> owned = DatatypePageMap.ComputeOwnedPages(
            ["", "  "],
            _ => true);

        Assert.Empty(owned);
    }
}
