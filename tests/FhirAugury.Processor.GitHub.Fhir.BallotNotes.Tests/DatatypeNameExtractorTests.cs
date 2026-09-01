using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

/// <summary>
/// Exercises <see cref="DatatypeNameExtractor"/>: per-datatype file extraction,
/// own-page reverse mapping (<c>references</c> / <c>metadatatypes</c>), the
/// variant/codesystem/valueset skip, and the aggregate-only HEAD enumeration.
/// </summary>
public sealed class DatatypeNameExtractorTests
{
    [Fact]
    public void Extracts_per_datatype_files_and_skips_variants()
    {
        IReadOnlyList<string> names = DatatypeNameExtractor.Extract(
            [
                "source/datatypes/Quantity.xml",
                "source/datatypes/Money.xml",
                "source/datatypes/codesystem-days-of-week.xml",  // skipped (has '-')
                "source/datatypes/address-extensions-spreadsheet.xml", // skipped
                "source/datatypes/_changelog.txt",               // skipped (not .xml)
            ],
            () => []);

        Assert.Equal(["Quantity", "Money"], names);
    }

    [Fact]
    public void Reverses_own_page_stems()
    {
        IReadOnlyList<string> names = DatatypeNameExtractor.Extract(
            ["source/references.html", "source/metadatatypes.html"],
            () => []);

        Assert.Contains("Reference", names);
        Assert.Contains("ContactDetail", names);
        Assert.Contains("UsageContext", names);
    }

    [Fact]
    public void Aggregate_only_change_enumerates_from_head()
    {
        IReadOnlyList<string> names = DatatypeNameExtractor.Extract(
            ["source/datatypes.html"],
            () => ["Quantity", "Money"]);

        Assert.Equal(["Quantity", "Money"], names);
    }

    [Fact]
    public void Per_datatype_files_suppress_head_enumeration()
    {
        IReadOnlyList<string> names = DatatypeNameExtractor.Extract(
            ["source/datatypes.html", "source/datatypes/Quantity.xml"],
            () => throw new InvalidOperationException("HEAD enumeration must not run"));

        Assert.Equal(["Quantity"], names);
    }

    [Fact]
    public void Dedupes_across_sources()
    {
        IReadOnlyList<string> names = DatatypeNameExtractor.Extract(
            ["source/datatypes/Quantity.xml", "source/quantity.html"],
            () => []);

        Assert.Single(names);
    }
}
