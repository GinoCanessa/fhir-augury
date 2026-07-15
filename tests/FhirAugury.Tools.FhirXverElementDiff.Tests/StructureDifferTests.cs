using FhirAugury.Tools.FhirXverElementDiff.Diff;
using FhirAugury.Tools.FhirXverElementDiff.Model;

namespace FhirAugury.Tools.FhirXverElementDiff.Tests;

public sealed class StructureDifferTests
{
    [Fact]
    public void Diff_Classifies_Mapped_Removed_Added()
    {
        ReleaseModel earlier = Tm.Release(
            ReleaseId.R4,
            Tm.Struct("Patient", "resource", Tm.Elem("Patient.active", 0, "1")),
            Tm.Struct("Media", "resource", Tm.Elem("Media.status", 0, "1")),
            Tm.Struct("HumanName", "complex-type", Tm.Elem("HumanName.text", 0, "1")));

        ReleaseModel later = Tm.Release(
            ReleaseId.R5,
            Tm.Struct("Patient", "resource", Tm.Elem("Patient.active", 0, "1")),
            Tm.Struct("Citation", "resource", Tm.Elem("Citation.url", 0, "1")),
            Tm.Struct("HumanName", "complex-type", Tm.Elem("HumanName.text", 0, "1")));

        StructureBuckets buckets = StructureDiffer.Diff(earlier, later);

        Assert.Contains(buckets.Mapped, p => p.Later.Name == "Patient" && !p.IsRename);
        Assert.Contains(buckets.Mapped, p => p.Later.Name == "HumanName");
        Assert.Contains(buckets.Removed, s => s.Name == "Media");
        Assert.Contains(buckets.Added, s => s.Name == "Citation");
        Assert.DoesNotContain(buckets.Removed, s => s.Name == "Patient");
    }

    [Fact]
    public void Diff_Groups_By_Kind()
    {
        ReleaseModel earlier = Tm.Release(
            ReleaseId.R4,
            Tm.Struct("Patient", "resource", Tm.Elem("Patient.active")),
            Tm.Struct("HumanName", "complex-type", Tm.Elem("HumanName.text")),
            Tm.Struct("boolean", "primitive-type", Tm.Elem("boolean.value")));

        ReleaseModel later = Tm.Release(
            ReleaseId.R5,
            Tm.Struct("Patient", "resource", Tm.Elem("Patient.active")),
            Tm.Struct("HumanName", "complex-type", Tm.Elem("HumanName.text")),
            Tm.Struct("boolean", "primitive-type", Tm.Elem("boolean.value")));

        StructureBuckets buckets = StructureDiffer.Diff(earlier, later);

        Assert.Single(buckets.MappedIn(StructureGroup.Resource));
        Assert.Single(buckets.MappedIn(StructureGroup.ComplexType));
        Assert.Single(buckets.MappedIn(StructureGroup.PrimitiveType));
    }
}
