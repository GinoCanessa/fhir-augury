using FhirAugury.Tools.FhirXverElementDiff.Model;

namespace FhirAugury.Tools.FhirXverElementDiff.Diff;

/// <summary>
/// Classifies every in-scope structure of two releases into Mapped / Removed / Added
/// by exact name. Rename resolution (which moves look-alikes out of Removed/Added into
/// Mapped) is a separate pass — see <see cref="StructureRenameDetector"/>.
/// </summary>
internal static class StructureDiffer
{
    public static StructureBuckets Diff(ReleaseModel earlier, ReleaseModel later)
    {
        Dictionary<string, StructureModel> laterByName =
            new(StringComparer.Ordinal);
        foreach (StructureModel s in later.Structures)
        {
            laterByName[s.Name] = s;
        }

        List<StructurePair> mapped = [];
        List<StructureModel> removed = [];
        HashSet<string> matchedLater = new(StringComparer.Ordinal);

        foreach (StructureModel e in earlier.Structures)
        {
            if (laterByName.TryGetValue(e.Name, out StructureModel? l))
            {
                mapped.Add(new StructurePair(e, l, RenameKind.None));
                matchedLater.Add(l.Name);
            }
            else
            {
                removed.Add(e);
            }
        }

        List<StructureModel> added = [];
        foreach (StructureModel l in later.Structures)
        {
            if (!matchedLater.Contains(l.Name))
            {
                added.Add(l);
            }
        }

        return new StructureBuckets(mapped, removed, added);
    }
}
