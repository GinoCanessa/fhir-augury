using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Grouping;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

public sealed class BallotNotesUnitGrouperTests
{
    private static readonly IReadOnlySet<string> NoOwnedPages = new HashSet<string>();

    [Fact]
    public void Group_routes_artifact_page_and_datatypes()
    {
        IReadOnlyList<HydrationUnit> units = BallotNotesUnitGrouper.Group(
            [
                "source/observation/observation.xml",
                "source/observation/observation-introduction.xml",
                "source/security.html",
                "source/datatypes/Quantity.xml",
                "source/datatypes.html",
            ],
            isFhirCore: true,
            NoOwnedPages);

        HydrationUnit datatypes = Assert.Single(units, u => u.Type == "DataType");
        Assert.Equal("datatypes", datatypes.Name);
        Assert.Contains("source/datatypes/Quantity.xml", datatypes.ChangedPaths);
        Assert.Contains("source/datatypes.html", datatypes.ChangedPaths);

        HydrationUnit artifact = Assert.Single(units, u => u.Type == "Artifact");
        Assert.Equal("observation", artifact.Name);
        Assert.Equal(2, artifact.ChangedPaths.Count);

        HydrationUnit page = Assert.Single(units, u => u.Type == "Page");
        Assert.Equal("security", page.Name);
    }

    [Fact]
    public void Group_routes_datatype_own_page_into_datatypes_not_page()
    {
        IReadOnlySet<string> owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "source/dosage.html",
        };

        IReadOnlyList<HydrationUnit> units = BallotNotesUnitGrouper.Group(
            [
                "source/dosage.html",
                "source/datatypes/Dosage.xml",
                "source/security.html",
            ],
            isFhirCore: true,
            owned);

        // dosage.html must NOT become its own page unit.
        Assert.DoesNotContain(units, u => u.Type == "Page" && u.Name == "dosage");

        HydrationUnit datatypes = Assert.Single(units, u => u.Type == "DataType");
        Assert.Contains("source/dosage.html", datatypes.ChangedPaths);

        // security.html is still a normal page.
        Assert.Single(units, u => u.Type == "Page" && u.Name == "security");
    }

    [Fact]
    public void Group_omits_datatypes_unit_when_not_fhir_core()
    {
        IReadOnlyList<HydrationUnit> units = BallotNotesUnitGrouper.Group(
            ["source/datatypes/Quantity.xml", "source/datatypes.html"],
            isFhirCore: false,
            NoOwnedPages);

        Assert.DoesNotContain(units, u => u.Type == "DataType");
    }

    [Fact]
    public void Group_treats_datatypes_html_as_page_when_not_fhir_core()
    {
        IReadOnlyList<HydrationUnit> units = BallotNotesUnitGrouper.Group(
            ["source/datatypes.html"],
            isFhirCore: false,
            NoOwnedPages);

        Assert.Single(units, u => u.Type == "Page" && u.Name == "datatypes");
    }

    [Fact]
    public void Group_ignores_paths_outside_source()
    {
        IReadOnlyList<HydrationUnit> units = BallotNotesUnitGrouper.Group(
            ["tools/whatever.txt", "publish/output.html", "README.md"],
            isFhirCore: true,
            NoOwnedPages);

        Assert.Empty(units);
    }
}
