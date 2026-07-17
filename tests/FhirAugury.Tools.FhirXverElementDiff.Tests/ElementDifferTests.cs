using FhirAugury.Tools.FhirXverElementDiff.Diff;
using FhirAugury.Tools.FhirXverElementDiff.Model;

namespace FhirAugury.Tools.FhirXverElementDiff.Tests;

public sealed class ElementDifferTests
{
    private static IReadOnlyList<ElementRow> Diff(
        StructureModel earlier, StructureModel later, RenameKind renameKind = RenameKind.None,
        StructureModel[]? earlierExtra = null, StructureModel[]? laterExtra = null)
    {
        ReleaseModel earlierRelease = Tm.Release(ReleaseId.R4, [earlier, .. earlierExtra ?? []]);
        ReleaseModel laterRelease = Tm.Release(ReleaseId.R5, [later, .. laterExtra ?? []]);
        StructurePair pair = new(earlier, later, renameKind);
        return ElementDiffer.Diff(pair, earlierRelease, laterRelease);
    }

    [Fact]
    public void Cardinality_Change_Emits_Single_Cardinality_Row()
    {
        IReadOnlyList<ElementRow> rows = Diff(
            Tm.Struct("Patient", "resource", Tm.Elem("Patient.gender", min: 0, max: "1")),
            Tm.Struct("Patient", "resource", Tm.Elem("Patient.gender", min: 1, max: "1")));

        ElementRow row = Assert.Single(rows);
        Assert.True(row.Flags.Cardinality);
        Assert.False(row.Flags.Type);
        Assert.False(row.Flags.Added);
        Assert.False(row.Flags.Removed);
        Assert.Contains("0..1 → 1..1", row.Summary);
    }

    [Fact]
    public void Type_Name_Change_Emits_Type_Row()
    {
        IReadOnlyList<ElementRow> rows = Diff(
            Tm.Struct("Foo", "complex-type", Tm.Elem("Foo.note", types: [Tm.T("string")])),
            Tm.Struct("Foo", "complex-type", Tm.Elem("Foo.note", types: [Tm.T("markdown")])));

        ElementRow row = Assert.Single(rows);
        Assert.True(row.Flags.Type);
        Assert.False(row.Flags.Cardinality);
        Assert.Contains("string → markdown", row.Summary);
    }

    [Fact]
    public void Type_Profile_Refinement_Emits_Type_Row_With_Profile_Summary()
    {
        IReadOnlyList<ElementRow> rows = Diff(
            Tm.Struct("Foo", "complex-type", Tm.Elem("Foo.value", types: [Tm.T("Quantity")])),
            Tm.Struct("Foo", "complex-type", Tm.Elem("Foo.value",
                types: [Tm.T("Quantity", "http://hl7.org/fhir/StructureDefinition/SimpleQuantity")])));

        ElementRow row = Assert.Single(rows);
        Assert.True(row.Flags.Type);
        Assert.Contains("+SimpleQuantity profile", row.Summary);
    }

    [Fact]
    public void Target_Profile_Addition_Emits_Target_Row()
    {
        IReadOnlyList<ElementRow> rows = Diff(
            Tm.Struct("Foo", "complex-type",
                Tm.Elem("Foo.subject", types: [Tm.T("Reference")], targets: ["Patient"])),
            Tm.Struct("Foo", "complex-type",
                Tm.Elem("Foo.subject", types: [Tm.T("Reference")], targets: ["Patient", "Group"])));

        ElementRow row = Assert.Single(rows);
        Assert.True(row.Flags.Target);
        Assert.False(row.Flags.Type);
        Assert.Contains("+Group target", row.Summary);
    }

    [Fact]
    public void Pure_Add_Emits_Added_Row()
    {
        IReadOnlyList<ElementRow> rows = Diff(
            Tm.Struct("Foo", "complex-type", Tm.Elem("Foo.keep")),
            Tm.Struct("Foo", "complex-type", Tm.Elem("Foo.keep"), Tm.Elem("Foo.extra")));

        ElementRow row = Assert.Single(rows);
        Assert.True(row.Flags.Added);
        Assert.Null(row.SourcePath);
        Assert.Equal("Foo.extra", row.TargetPath);
        Assert.Contains("Added in R5", row.Summary);
    }

    [Fact]
    public void Pure_Remove_Emits_Removed_Row()
    {
        IReadOnlyList<ElementRow> rows = Diff(
            Tm.Struct("Foo", "complex-type", Tm.Elem("Foo.keep"), Tm.Elem("Foo.old")),
            Tm.Struct("Foo", "complex-type", Tm.Elem("Foo.keep")));

        ElementRow row = Assert.Single(rows);
        Assert.True(row.Flags.Removed);
        Assert.Equal("Foo.old", row.SourcePath);
        Assert.Null(row.TargetPath);
        Assert.Contains("Removed in R5", row.Summary);
    }

    [Fact]
    public void No_Change_Yields_No_Row()
    {
        IReadOnlyList<ElementRow> rows = Diff(
            Tm.Struct("Foo", "complex-type", Tm.Elem("Foo.same", min: 0, max: "1", types: [Tm.T("string")])),
            Tm.Struct("Foo", "complex-type", Tm.Elem("Foo.same", min: 0, max: "1", types: [Tm.T("string")])));

        Assert.Empty(rows);
    }

    [Fact]
    public void Locally_Constrained_Inherited_Element_Is_Kept()
    {
        // xhtml.id: R5 0..1 (== base Element.id, purely inherited) → R6 0..0 (locally constrained).
        StructureModel elementBase = Tm.Struct("Element", "complex-type", Tm.Elem("Element.id", min: 0, max: "1"));
        StructureModel xhtmlEarlier = Tm.Struct("xhtml", "primitive-type",
            Tm.Elem("xhtml.id", min: 0, max: "1", inherited: true, basePath: "Element.id"));
        StructureModel xhtmlLater = Tm.Struct("xhtml", "primitive-type",
            Tm.Elem("xhtml.id", min: 0, max: "0", inherited: true, basePath: "Element.id"));

        IReadOnlyList<ElementRow> rows = Diff(
            xhtmlEarlier, xhtmlLater,
            earlierExtra: [elementBase], laterExtra: [elementBase]);

        ElementRow row = Assert.Single(rows);
        Assert.True(row.Flags.Cardinality);
        Assert.Contains("0..1 → 0..0", row.Summary);
    }

    [Fact]
    public void Choice_Narrowing_Is_Single_Type_Row_Not_Add_Remove()
    {
        // doseNumber[x] (positiveInt|string) → doseNumber (positiveInt): same normalized key.
        IReadOnlyList<ElementRow> rows = Diff(
            Tm.Struct("Immunization", "resource",
                Tm.Elem("Immunization.doseNumber[x]", types: [Tm.T("positiveInt"), Tm.T("string")])),
            Tm.Struct("Immunization", "resource",
                Tm.Elem("Immunization.doseNumber", types: [Tm.T("positiveInt")])));

        ElementRow row = Assert.Single(rows);
        Assert.True(row.Flags.Type);
        Assert.False(row.Flags.Added);
        Assert.False(row.Flags.Removed);
    }

    [Fact]
    public void Renamed_Structure_Elements_Diff_Root_Relative()
    {
        IReadOnlyList<ElementRow> rows = Diff(
            Tm.Struct("DeviceUseStatement", "resource", Tm.Elem("DeviceUseStatement.status", min: 1, max: "1")),
            Tm.Struct("DeviceUsage", "resource", Tm.Elem("DeviceUsage.status", min: 0, max: "1")),
            renameKind: RenameKind.Confirmed);

        ElementRow row = Assert.Single(rows);
        Assert.True(row.Flags.Cardinality);
        Assert.False(row.Flags.Added);
        Assert.False(row.Flags.Removed);
        Assert.Equal("DeviceUseStatement.status", row.SourcePath);
        Assert.Equal("DeviceUsage.status", row.TargetPath);
    }

    [Fact]
    public void Renamed_Backbone_Subtree_Is_Not_All_Remove_Add()
    {
        StructureModel earlier = Tm.Struct("Foo", "complex-type",
            Tm.Elem("Foo.old", types: [Tm.T("BackboneElement")]),
            Tm.Elem("Foo.old.value", min: 1, max: "1", types: [Tm.T("string")]));
        StructureModel later = Tm.Struct("Foo", "complex-type",
            Tm.Elem("Foo.new", types: [Tm.T("BackboneElement")]),
            Tm.Elem("Foo.new.value", min: 1, max: "1", types: [Tm.T("string")]));

        IReadOnlyList<ElementRow> rows = Diff(earlier, later);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(RenameKind.Suspected, r.Flags.Renamed));
        Assert.All(rows, r => Assert.False(r.Flags.Added));
        Assert.All(rows, r => Assert.False(r.Flags.Removed));
        Assert.Contains(rows, r => r.SourcePath == "Foo.old" && r.TargetPath == "Foo.new");
        Assert.Contains(rows, r => r.SourcePath == "Foo.old.value" && r.TargetPath == "Foo.new.value");
    }

    [Fact]
    public void Leaf_Suspected_Rename_When_Facets_Match_Uniquely()
    {
        IReadOnlyList<ElementRow> rows = Diff(
            Tm.Struct("Foo", "complex-type", Tm.Elem("Foo.alpha", min: 1, max: "1", types: [Tm.T("string")])),
            Tm.Struct("Foo", "complex-type", Tm.Elem("Foo.beta", min: 1, max: "1", types: [Tm.T("string")])));

        ElementRow row = Assert.Single(rows);
        Assert.Equal(RenameKind.Suspected, row.Flags.Renamed);
        Assert.Equal("Foo.alpha", row.SourcePath);
        Assert.Equal("Foo.beta", row.TargetPath);
        Assert.Contains("renamed from alpha", row.Summary);
    }

    [Fact]
    public void Choice_Split_Is_Annotated_Not_Force_Paired()
    {
        IReadOnlyList<ElementRow> rows = Diff(
            Tm.Struct("Foo", "complex-type",
                Tm.Elem("Foo.value[x]", types: [Tm.T("string"), Tm.T("Quantity")])),
            Tm.Struct("Foo", "complex-type",
                Tm.Elem("Foo.valueString", types: [Tm.T("string")]),
                Tm.Elem("Foo.valueQuantity", types: [Tm.T("Quantity")])));

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(RenameKind.None, r.Flags.Renamed));
        Assert.All(rows, r => Assert.Contains("(choice split)", r.Summary));
        Assert.Single(rows, r => r.Flags.Removed && r.SourcePath == "Foo.value[x]");
        Assert.Equal(2, rows.Count(r => r.Flags.Added));
    }
}
