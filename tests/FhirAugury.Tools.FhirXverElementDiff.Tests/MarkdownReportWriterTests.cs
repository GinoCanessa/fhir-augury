using FhirAugury.Tools.FhirXverElementDiff.Diff;
using FhirAugury.Tools.FhirXverElementDiff.Model;
using FhirAugury.Tools.FhirXverElementDiff.Report;

namespace FhirAugury.Tools.FhirXverElementDiff.Tests;

public sealed class MarkdownReportWriterTests
{
    private static ReportModel BuildModel()
    {
        ReportHeader header = new(
            GeneratedUtc: new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
            EarlierLabel: "R4B",
            LaterLabel: "R5",
            EarlierVersion: "4.3.0",
            LaterVersion: "5.0.0",
            EarlierBuilt: "2025-10-31",
            LaterBuilt: "2025-10-31",
            SinceSha: "959acd13",
            UntilSha: "eca054db",
            CloneHead: "94dbe68f",
            AttributionEnabled: false,
            HeaderNote: null);

        // A resource mapped structure with a cardinality row and a pipe-bearing type row.
        StructurePair patient = new(
            Tm.Struct("Patient", "resource", Tm.Elem("Patient.gender")),
            Tm.Struct("Patient", "resource", Tm.Elem("Patient.gender")),
            RenameKind.None);
        List<ElementRow> patientRows =
        [
            new ElementRow("Patient.gender", "Patient.gender",
                new ElementFlags(false, false, RenameKind.None, true, false, false), "0..1 → 1..1"),
            new ElementRow("Patient.deceased[x]", "Patient.deceased[x]",
                new ElementFlags(false, false, RenameKind.None, false, true, false), "Quantity|string → Quantity"),
        ];

        // A confirmed structure rename with a suspected element-rename row.
        StructurePair device = new(
            Tm.Struct("DeviceUseStatement", "resource", Tm.Elem("DeviceUseStatement.status")),
            Tm.Struct("DeviceUsage", "resource", Tm.Elem("DeviceUsage.status")),
            RenameKind.Confirmed);
        List<ElementRow> deviceRows =
        [
            new ElementRow("DeviceUseStatement.subject", "DeviceUsage.patient",
                new ElementFlags(false, false, RenameKind.Suspected, false, false, false), "renamed from subject"),
        ];

        // A primitive mapped structure to exercise the primitive-types group heading.
        StructurePair boolean = new(
            Tm.Struct("boolean", "primitive-type", Tm.Elem("boolean.value")),
            Tm.Struct("boolean", "primitive-type", Tm.Elem("boolean.value")),
            RenameKind.None);
        List<ElementRow> booleanRows =
        [
            new ElementRow("boolean.value", "boolean.value",
                new ElementFlags(false, false, RenameKind.None, false, true, false), "boolean → boolean"),
        ];

        return new ReportModel(
            Increments.R4BToR5,
            header,
            Mapped:
            [
                new MappedStructureReport(boolean, booleanRows),
                new MappedStructureReport(patient, patientRows),
                new MappedStructureReport(device, deviceRows),
            ],
            Removed:
            [
                new StructureElementReport(
                    Tm.Struct("Media", "resource", Tm.Elem("Media.status")),
                    [new ElementRow("Media.status", null, new ElementFlags(false, true, RenameKind.None, false, false, false), "Removed in R5")]),
            ],
            Added:
            [
                new StructureElementReport(
                    Tm.Struct("Citation", "resource", Tm.Elem("Citation.status")),
                    [new ElementRow(null, "Citation.status", new ElementFlags(true, false, RenameKind.None, false, false, false), "Added in R5")]),
            ]);
    }

    [Fact]
    public void Renders_Title_Header_And_Table_Header()
    {
        string md = MarkdownReportWriter.Render(BuildModel());

        Assert.Contains("# FHIR element changes: R4B → R5", md);
        Assert.Contains("| Earlier — R4B | 4.3.0 | 2025-10-31 |", md);
        Assert.Contains("`959acd13`..`eca054db` (clone HEAD `94dbe68f`)", md);
        Assert.Contains("disabled (`--no-attribution`)", md);
        Assert.Contains(MarkdownReportWriter.TableHeader, md);
    }

    [Fact]
    public void Renders_Three_Sections_And_Group_Headings_In_Order()
    {
        string md = MarkdownReportWriter.Render(BuildModel());

        int mapped = md.IndexOf("## Mapped", StringComparison.Ordinal);
        int removed = md.IndexOf("## Removed", StringComparison.Ordinal);
        int added = md.IndexOf("## Added", StringComparison.Ordinal);
        Assert.True(mapped >= 0 && mapped < removed && removed < added);

        // Primitive-types group precedes resources within Mapped.
        int primitives = md.IndexOf("### Primitive types", StringComparison.Ordinal);
        int resources = md.IndexOf("### Resources", StringComparison.Ordinal);
        Assert.True(primitives >= 0 && primitives < resources);
    }

    [Fact]
    public void Renders_Flags_Rename_Headings_And_Escapes_Pipes()
    {
        string md = MarkdownReportWriter.Render(BuildModel());

        Assert.Contains("#### Patient", md);
        Assert.Contains("#### DeviceUsage (renamed from DeviceUseStatement)", md);
        Assert.Contains("#### Media", md);
        Assert.Contains("#### Citation", md);

        // Suspected element rename renders Y? and the warning marker.
        Assert.Contains("| Y? |", md);
        Assert.Contains("⚠ suspected", md);

        // Pipe inside a type summary is escaped so the table stays intact.
        Assert.Contains("Quantity\\|string → Quantity", md);

        // Change-record cells render as an em dash until attribution lands.
        Assert.Contains("| — |", md);
    }

    [Fact]
    public void Renders_Cardinality_Flag_As_Y()
    {
        string md = MarkdownReportWriter.Render(BuildModel());

        string cardinalityLine = md
            .Split('\n')
            .Single(l => l.Contains("0..1 → 1..1", StringComparison.Ordinal));
        // Columns: Source | Target | Added | Removed | Renamed | Cardinality | Type | Target | Summary | Change record
        string[] cells = cardinalityLine.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        Assert.Equal("Y", cells[5]);   // Cardinality
        Assert.Equal(string.Empty, cells[6]); // Type
    }
}
