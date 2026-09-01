using FhirAugury.Tools.FhirXverElementDiff.Attribution;
using FhirAugury.Tools.FhirXverElementDiff.Diff;
using FhirAugury.Tools.FhirXverElementDiff.Model;
using FhirAugury.Tools.FhirXverElementDiff.Readers;

namespace FhirAugury.Tools.FhirXverElementDiff.Tests;

/// <summary>
/// Unit tests for the Phase 6 hybrid (per-element) attribution: the diff <see cref="PatchParser"/>
/// (element-scoped cardinality/structural touches, base-block suppression, description no-ops) and
/// the isolating-commit selection in <see cref="Attributor"/> — including the R5→R6 ballot4
/// snapshot cardinality gate and newest-wins ordering. All synthetic; no git or DB required.
/// </summary>
public sealed class HybridAttributionTests
{
    private static string Patch(params string[] lines) => string.Join("\n", lines) + "\n";

    // A cardinality change (min 0→1) on Foo.bar, with the enclosing <path> in context.
    private static readonly string CardMinPatch = Patch(
        "diff --git a/source/foo/structuredefinition-Foo.xml b/source/foo/structuredefinition-Foo.xml",
        "--- a/source/foo/structuredefinition-Foo.xml",
        "+++ b/source/foo/structuredefinition-Foo.xml",
        "@@ -10,7 +10,7 @@",
        "     <element id=\"Foo.bar\">",
        "       <path value=\"Foo.bar\"/>",
        "       <short value=\"the bar\"/>",
        "-      <min value=\"0\"/>",
        "+      <min value=\"1\"/>",
        "       <max value=\"1\"/>",
        "     </element>");

    // max 1→0 on Foo.bar (the ballot4 constraint).
    private static readonly string CardMaxTo0Patch = Patch(
        "diff --git a/source/foo/structuredefinition-Foo.xml b/source/foo/structuredefinition-Foo.xml",
        "--- a/source/foo/structuredefinition-Foo.xml",
        "+++ b/source/foo/structuredefinition-Foo.xml",
        "@@ -10,7 +10,7 @@",
        "     <element id=\"Foo.bar\">",
        "       <path value=\"Foo.bar\"/>",
        "       <min value=\"0\"/>",
        "-      <max value=\"1\"/>",
        "+      <max value=\"0\"/>",
        "     </element>");

    // max 0→1 on Foo.bar (a later, post-snapshot over-write).
    private static readonly string CardMaxTo1Patch = Patch(
        "diff --git a/source/foo/structuredefinition-Foo.xml b/source/foo/structuredefinition-Foo.xml",
        "--- a/source/foo/structuredefinition-Foo.xml",
        "+++ b/source/foo/structuredefinition-Foo.xml",
        "@@ -10,7 +10,7 @@",
        "     <element id=\"Foo.bar\">",
        "       <path value=\"Foo.bar\"/>",
        "       <min value=\"0\"/>",
        "-      <max value=\"0\"/>",
        "+      <max value=\"1\"/>",
        "     </element>");

    // A description-only edit on Foo.bar (must not register any facet touch).
    private static readonly string DescriptionPatch = Patch(
        "diff --git a/source/foo/structuredefinition-Foo.xml b/source/foo/structuredefinition-Foo.xml",
        "--- a/source/foo/structuredefinition-Foo.xml",
        "+++ b/source/foo/structuredefinition-Foo.xml",
        "@@ -10,7 +10,7 @@",
        "     <element id=\"Foo.bar\">",
        "       <path value=\"Foo.bar\"/>",
        "-      <short value=\"old text\"/>",
        "+      <short value=\"new text\"/>",
        "       <min value=\"0\"/>",
        "       <max value=\"1\"/>",
        "     </element>");

    // A change to the <base> sub-block's min — belongs to the base, not the element itself.
    private static readonly string BaseMinPatch = Patch(
        "diff --git a/source/foo/structuredefinition-Foo.xml b/source/foo/structuredefinition-Foo.xml",
        "--- a/source/foo/structuredefinition-Foo.xml",
        "+++ b/source/foo/structuredefinition-Foo.xml",
        "@@ -10,10 +10,10 @@",
        "     <element id=\"Foo.bar\">",
        "       <path value=\"Foo.bar\"/>",
        "       <min value=\"1\"/>",
        "       <max value=\"1\"/>",
        "       <base>",
        "         <path value=\"Foo.bar\"/>",
        "-        <min value=\"0\"/>",
        "+        <min value=\"1\"/>",
        "         <max value=\"1\"/>",
        "       </base>",
        "     </element>");

    // A new element (Foo.baz) added after the stable Foo.name.
    private static readonly string AddElementPatch = Patch(
        "diff --git a/source/foo/structuredefinition-Foo.xml b/source/foo/structuredefinition-Foo.xml",
        "--- a/source/foo/structuredefinition-Foo.xml",
        "+++ b/source/foo/structuredefinition-Foo.xml",
        "@@ -20,6 +20,11 @@",
        "     <element id=\"Foo.name\">",
        "       <path value=\"Foo.name\"/>",
        "       <max value=\"1\"/>",
        "     </element>",
        "+    <element id=\"Foo.baz\">",
        "+      <path value=\"Foo.baz\"/>",
        "+      <min value=\"0\"/>",
        "+      <max value=\"1\"/>",
        "+    </element>");

    private static FhirKeyAllowlist Allow(params int[] numbers) => new([.. numbers]);

    private static CommitPatch Cp(string sha, string subject, string patch) =>
        new(new CommitInfo(sha, sha, subject, string.Empty), patch);

    private static ElementRow Row(string path, bool cardinality = false, bool added = false,
        bool removed = false, string summary = "") =>
        new(
            added ? null : path,
            removed ? null : path,
            new ElementFlags(added, removed, RenameKind.None, cardinality, false, false),
            summary);

    [Fact]
    public void Cardinality_Change_Registers_An_Element_Scoped_Touch()
    {
        IReadOnlyList<ElementTouch> touches = PatchParser.Parse(CardMinPatch);

        Assert.All(touches, t => Assert.Equal("Foo.bar", t.Path));
        Assert.Contains(touches, t => t.Facet == ElementFacet.Cardinality && t.NewMin == "1");
    }

    [Fact]
    public void Description_Only_Edit_Registers_No_Touch()
    {
        Assert.Empty(PatchParser.Parse(DescriptionPatch));
    }

    [Fact]
    public void Base_Block_Cardinality_Is_Not_Attributed_To_The_Element()
    {
        // The element's own min/max are context here; only the <base> min changes — and that
        // is the base's facet, not the element's, so nothing is registered.
        Assert.Empty(PatchParser.Parse(BaseMinPatch));
    }

    [Fact]
    public void Element_Add_Registers_A_Structural_Touch()
    {
        IReadOnlyList<ElementTouch> touches = PatchParser.Parse(AddElementPatch);

        Assert.Contains(touches, t => t.Path == "Foo.baz" && t.Facet == ElementFacet.Structural);
    }

    [Fact]
    public void Isolating_Commit_Attributes_Its_Row_While_Sibling_Keeps_The_Window()
    {
        Dictionary<string, List<Attributor.PathTouch>> index =
            Attributor.BuildElementIndex([Cp("s1", "#100 Foo.bar cardinality", CardMinPatch)], Allow(100));
        Attributor.StructureAttribution attr = new(new ElementChangeRecord(["FHIR-999"], []), index);

        ElementChangeRecord? bar = Attributor.ResolvePerElement(
            Row("Foo.bar", cardinality: true, summary: "0..1 → 1..1"), attr, isR6Target: false);
        Assert.Equal(["FHIR-100"], bar!.TicketKeys);

        // A co-changed sibling the commit did not touch gets no per-element record, so
        // ApplyReport keeps the shared structure-window record for it.
        ElementChangeRecord? baz = Attributor.ResolvePerElement(
            Row("Foo.baz", cardinality: true, summary: "0..1 → 1..1"), attr, isR6Target: false);
        Assert.Null(baz);
    }

    [Fact]
    public void Description_Commit_Does_Not_Claim_The_Cardinality_Row()
    {
        Dictionary<string, List<Attributor.PathTouch>> index =
            Attributor.BuildElementIndex([Cp("s1", "#100 Foo.bar docs", DescriptionPatch)], Allow(100));

        Assert.False(index.ContainsKey("Foo.bar"));

        Attributor.StructureAttribution attr = new(new ElementChangeRecord(["FHIR-999"], []), index);
        Assert.Null(Attributor.ResolvePerElement(
            Row("Foo.bar", cardinality: true, summary: "0..1 → 1..1"), attr, isR6Target: false));
    }

    [Fact]
    public void R6_Gate_Rejects_The_Post_Snapshot_Overwrite_And_Picks_The_Snapshot_Commit()
    {
        // Newest first: #200 sets max to 1 (after the snapshot); #100 sets max to 0 (the ballot4
        // value the DB carries). The row's target is 0..0.
        Dictionary<string, List<Attributor.PathTouch>> index = Attributor.BuildElementIndex(
            [
                Cp("s2", "#200 later tweak", CardMaxTo1Patch),
                Cp("s1", "#100 ballot4 constrain", CardMaxTo0Patch),
            ],
            Allow(100, 200));
        Attributor.StructureAttribution attr = new(new ElementChangeRecord(["FHIR-999"], []), index);
        ElementRow row = Row("Foo.bar", cardinality: true, summary: "0..1 → 0..0");

        // With the gate on, the post-snapshot #200 (max 1 ≠ target 0) is skipped for #100.
        ElementChangeRecord? gated = Attributor.ResolvePerElement(row, attr, isR6Target: true);
        Assert.Equal(["FHIR-100"], gated!.TicketKeys);

        // With the gate off (non-R6 increments), the newest isolating commit simply wins.
        ElementChangeRecord? newest = Attributor.ResolvePerElement(row, attr, isR6Target: false);
        Assert.Equal(["FHIR-200"], newest!.TicketKeys);
    }

    [Fact]
    public void Broad_Sweep_Commit_Is_Not_Used_For_Per_Element_Attribution()
    {
        // Five distinct elements changed in one commit → over the isolation limit → ignored.
        string sweep = Patch(
            "diff --git a/source/foo/structuredefinition-Foo.xml b/source/foo/structuredefinition-Foo.xml",
            "--- a/source/foo/structuredefinition-Foo.xml",
            "+++ b/source/foo/structuredefinition-Foo.xml",
            "@@ -10,20 +10,20 @@",
            "       <path value=\"Foo.a\"/>",
            "-      <min value=\"0\"/>",
            "+      <min value=\"1\"/>",
            "       <path value=\"Foo.b\"/>",
            "-      <min value=\"0\"/>",
            "+      <min value=\"1\"/>",
            "       <path value=\"Foo.c\"/>",
            "-      <min value=\"0\"/>",
            "+      <min value=\"1\"/>",
            "       <path value=\"Foo.d\"/>",
            "-      <min value=\"0\"/>",
            "+      <min value=\"1\"/>",
            "       <path value=\"Foo.e\"/>",
            "-      <min value=\"0\"/>",
            "+      <min value=\"1\"/>");

        Dictionary<string, List<Attributor.PathTouch>> index =
            Attributor.BuildElementIndex([Cp("s1", "#100 sweeping rework", sweep)], Allow(100));

        Assert.Empty(index);
    }
}
