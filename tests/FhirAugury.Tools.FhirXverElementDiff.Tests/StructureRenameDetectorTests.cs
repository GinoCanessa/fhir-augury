using FhirAugury.Tools.FhirXverElementDiff.Diff;
using FhirAugury.Tools.FhirXverElementDiff.Model;

namespace FhirAugury.Tools.FhirXverElementDiff.Tests;

public sealed class StructureRenameDetectorTests
{
    // DeviceUseStatement → DeviceUsage share only ~0.29 field Jaccard, so this pair
    // MUST resolve via the curated map (Confirmed), never the heuristic (Suspected).
    private static StructureModel DeviceUseStatement() => Tm.Struct(
        "DeviceUseStatement", "resource",
        Tm.Elem("DeviceUseStatement.status"),
        Tm.Elem("DeviceUseStatement.subject"),
        Tm.Elem("DeviceUseStatement.device"),
        Tm.Elem("DeviceUseStatement.timing"));

    private static StructureModel DeviceUsage() => Tm.Struct(
        "DeviceUsage", "resource",
        Tm.Elem("DeviceUsage.status"),
        Tm.Elem("DeviceUsage.patient"),
        Tm.Elem("DeviceUsage.device"),
        Tm.Elem("DeviceUsage.context"),
        Tm.Elem("DeviceUsage.dateAsserted"));

    [Fact]
    public void CuratedMap_Resolves_Confirmed_Rename()
    {
        StructureBuckets buckets = new(
            mapped: [],
            removed: [DeviceUseStatement()],
            added: [DeviceUsage()]);

        new StructureRenameDetector().Apply(buckets);

        StructurePair pair = Assert.Single(buckets.Mapped);
        Assert.Equal(RenameKind.Confirmed, pair.RenameKind);
        Assert.Equal("DeviceUseStatement", pair.OldName);
        Assert.Equal("DeviceUsage", pair.Later.Name);
        Assert.Empty(buckets.Removed);
        Assert.Empty(buckets.Added);
    }

    [Fact]
    public void Heuristic_Resolves_Suspected_For_LookAlike()
    {
        // 3 shared of 5 union = 0.6 Jaccard, above the 0.5 threshold, but absent from
        // the curated map → Suspected only.
        StructureModel oldStruct = Tm.Struct(
            "WidgetOld", "resource",
            Tm.Elem("WidgetOld.a"), Tm.Elem("WidgetOld.b"),
            Tm.Elem("WidgetOld.c"), Tm.Elem("WidgetOld.d"));
        StructureModel newStruct = Tm.Struct(
            "WidgetNew", "resource",
            Tm.Elem("WidgetNew.a"), Tm.Elem("WidgetNew.b"),
            Tm.Elem("WidgetNew.c"), Tm.Elem("WidgetNew.e"));

        StructureBuckets buckets = new(mapped: [], removed: [oldStruct], added: [newStruct]);

        new StructureRenameDetector().Apply(buckets);

        StructurePair pair = Assert.Single(buckets.Mapped);
        Assert.Equal(RenameKind.Suspected, pair.RenameKind);
        Assert.Equal("WidgetOld", pair.OldName);
    }

    [Fact]
    public void DeviceUseStatement_Uses_CuratedMap_Not_Heuristic()
    {
        StructureBuckets buckets = new(
            mapped: [], removed: [DeviceUseStatement()], added: [DeviceUsage()]);

        // Even with the heuristic effectively disabled (threshold 1.0), the curated
        // map still resolves the rename — proving it did not rely on field similarity.
        new StructureRenameDetector(heuristicThreshold: 1.0).Apply(buckets);

        StructurePair pair = Assert.Single(buckets.Mapped);
        Assert.Equal(RenameKind.Confirmed, pair.RenameKind);
    }

    [Fact]
    public void Unrelated_Structures_Stay_In_Removed_And_Added()
    {
        StructureModel removed = Tm.Struct("CatalogEntry", "resource",
            Tm.Elem("CatalogEntry.type"), Tm.Elem("CatalogEntry.status"));
        StructureModel added = Tm.Struct("GenomicStudy", "resource",
            Tm.Elem("GenomicStudy.subject"), Tm.Elem("GenomicStudy.analysis"));

        StructureBuckets buckets = new(mapped: [], removed: [removed], added: [added]);

        new StructureRenameDetector().Apply(buckets);

        Assert.Empty(buckets.Mapped);
        Assert.Contains(buckets.Removed, s => s.Name == "CatalogEntry");
        Assert.Contains(buckets.Added, s => s.Name == "GenomicStudy");
    }

    [Fact]
    public void Heuristic_Only_Matches_Same_Kind()
    {
        StructureModel removedResource = Tm.Struct("AlphaOld", "resource",
            Tm.Elem("AlphaOld.a"), Tm.Elem("AlphaOld.b"), Tm.Elem("AlphaOld.c"));
        StructureModel addedComplex = Tm.Struct("AlphaNew", "complex-type",
            Tm.Elem("AlphaNew.a"), Tm.Elem("AlphaNew.b"), Tm.Elem("AlphaNew.c"));

        StructureBuckets buckets = new(mapped: [], removed: [removedResource], added: [addedComplex]);

        new StructureRenameDetector().Apply(buckets);

        Assert.Empty(buckets.Mapped);
        Assert.Single(buckets.Removed);
        Assert.Single(buckets.Added);
    }
}
