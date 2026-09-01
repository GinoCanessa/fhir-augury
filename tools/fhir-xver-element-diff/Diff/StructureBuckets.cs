using FhirAugury.Tools.FhirXverElementDiff.Model;

namespace FhirAugury.Tools.FhirXverElementDiff.Diff;

/// <summary>Whether a mapped structure pair is a plain match or a rename, and how confident.</summary>
internal enum RenameKind
{
    None,
    Confirmed,
    Suspected,
}

/// <summary>
/// A structure that exists in both releases (Mapped bucket), possibly under a
/// different name (a rename). For a rename, <see cref="Earlier"/> and
/// <see cref="Later"/> carry the old and new names respectively.
/// </summary>
internal sealed record StructurePair(
    StructureModel Earlier,
    StructureModel Later,
    RenameKind RenameKind)
{
    public bool IsRename => RenameKind != RenameKind.None;

    /// <summary>The earlier (old) name when this pair is a rename; otherwise null.</summary>
    public string? OldName => IsRename ? Earlier.Name : null;

    /// <summary>Report grouping is driven by the later structure's kind.</summary>
    public StructureGroup Group => Later.Group;

    /// <summary>The name shown as the structure heading (the later/new name).</summary>
    public string DisplayName => Later.Name;
}

/// <summary>
/// The Mapped / Removed / Added classification of every in-scope structure across an
/// increment. Lists are mutable so <see cref="StructureRenameDetector"/> can move a
/// rename pair out of Removed/Added into Mapped.
/// </summary>
internal sealed class StructureBuckets
{
    public StructureBuckets(
        List<StructurePair> mapped, List<StructureModel> removed, List<StructureModel> added)
    {
        Mapped = mapped;
        Removed = removed;
        Added = added;
    }

    public List<StructurePair> Mapped { get; }
    public List<StructureModel> Removed { get; }
    public List<StructureModel> Added { get; }

    public IEnumerable<StructurePair> MappedIn(StructureGroup group) =>
        Mapped.Where(p => p.Group == group).OrderBy(p => p.DisplayName, StringComparer.Ordinal);

    public IEnumerable<StructureModel> RemovedIn(StructureGroup group) =>
        Removed.Where(s => s.Group == group).OrderBy(s => s.Name, StringComparer.Ordinal);

    public IEnumerable<StructureModel> AddedIn(StructureGroup group) =>
        Added.Where(s => s.Group == group).OrderBy(s => s.Name, StringComparer.Ordinal);
}
