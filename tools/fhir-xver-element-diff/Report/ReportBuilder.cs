using FhirAugury.Tools.FhirXverElementDiff.Diff;
using FhirAugury.Tools.FhirXverElementDiff.Model;

namespace FhirAugury.Tools.FhirXverElementDiff.Report;

/// <summary>
/// Assembles a <see cref="ReportModel"/> from two loaded releases: buckets the structures,
/// resolves renames, diffs each mapped structure's elements (keeping only structures that
/// actually changed), and renders every removed/added structure's locally-meaningful
/// elements as all-Removed / all-Added rows. Pure and deterministic — no I/O.
/// </summary>
internal static class ReportBuilder
{
    public static ReportModel Build(
        IncrementDefinition increment,
        ReleaseModel earlier,
        ReleaseModel later,
        ReportHeader header)
    {
        StructureBuckets buckets = StructureDiffer.Diff(earlier, later);
        new StructureRenameDetector().Apply(buckets);

        List<MappedStructureReport> mapped = [];
        foreach (StructurePair pair in buckets.Mapped
            .OrderBy(p => p.Group)
            .ThenBy(p => p.DisplayName, StringComparer.Ordinal))
        {
            IReadOnlyList<ElementRow> rows = ElementDiffer.Diff(pair, earlier, later);
            if (rows.Count > 0)
            {
                mapped.Add(new MappedStructureReport(pair, rows));
            }
        }

        string laterLabel = Release.DisplayLabel(later.Release.Id);

        List<StructureElementReport> removed = [];
        foreach (StructureModel structure in buckets.Removed
            .OrderBy(s => s.Group)
            .ThenBy(s => s.Name, StringComparer.Ordinal))
        {
            removed.Add(new StructureElementReport(
                structure, AllElementRows(earlier, structure, laterLabel, added: false)));
        }

        List<StructureElementReport> added = [];
        foreach (StructureModel structure in buckets.Added
            .OrderBy(s => s.Group)
            .ThenBy(s => s.Name, StringComparer.Ordinal))
        {
            added.Add(new StructureElementReport(
                structure, AllElementRows(later, structure, laterLabel, added: true)));
        }

        return new ReportModel(increment, header, mapped, removed, added);
    }

    private static IReadOnlyList<ElementRow> AllElementRows(
        ReleaseModel release, StructureModel structure, string laterLabel, bool added)
    {
        List<ElementRow> rows = [];
        foreach (ElementModel element in release.MeaningfulElements(structure))
        {
            ElementFlags flags = added
                ? new ElementFlags(true, false, RenameKind.None, false, false, false)
                : new ElementFlags(false, true, RenameKind.None, false, false, false);
            string summary = added ? $"Added in {laterLabel}" : $"Removed in {laterLabel}";
            rows.Add(new ElementRow(
                added ? null : element.Path,
                added ? element.Path : null,
                flags,
                summary));
        }
        rows.Sort(static (a, b) => string.CompareOrdinal(a.SortPath, b.SortPath));
        return rows;
    }
}
