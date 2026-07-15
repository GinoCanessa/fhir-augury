using FhirAugury.Tools.FhirXverElementDiff.Model;

namespace FhirAugury.Tools.FhirXverElementDiff.Diff;

/// <summary>
/// Diffs the elements of one mapped structure pair by <see cref="ElementModel.NormalizedKey"/>
/// (root-relative, choice-<c>[x]</c> folded) rather than raw path, so a renamed structure or
/// choice narrowing compares correctly. Applies the base-aware inheritance filter
/// (union-of-interestingness): a normalized key participates when it is locally meaningful
/// (not purely inherited) on <em>either</em> side; the raw snapshot values from both sides are
/// then compared so a locally-constrained-then-reverted element (e.g. <c>xhtml.id</c>
/// <c>0..1</c>→<c>0..0</c>) still surfaces as a cardinality change. Residual pure add/remove
/// leftovers are handed to <see cref="ElementRenameDetector"/> for element-level rename and
/// choice split/merge resolution. Emits a row only when at least one flag is set (decision #7).
/// </summary>
internal static class ElementDiffer
{
    public static IReadOnlyList<ElementRow> Diff(
        StructurePair pair,
        ReleaseModel earlierRelease,
        ReleaseModel laterRelease,
        IReadOnlySet<(string OldKey, string NewKey)>? confirmedFieldRenames = null)
    {
        Dictionary<string, ElementModel> earlierByKey =
            BuildIndex(pair.Earlier, earlierRelease, out HashSet<string> earlierMeaningful);
        Dictionary<string, ElementModel> laterByKey =
            BuildIndex(pair.Later, laterRelease, out HashSet<string> laterMeaningful);

        SortedSet<string> candidates = new(StringComparer.Ordinal);
        candidates.UnionWith(earlierMeaningful);
        candidates.UnionWith(laterMeaningful);

        string laterLabel = Release.DisplayLabel(laterRelease.Release.Id);

        List<ElementRow> rows = [];
        List<ElementModel> removedLeftovers = [];
        List<ElementModel> addedLeftovers = [];

        foreach (string key in candidates)
        {
            ElementModel? earlier = earlierByKey.GetValueOrDefault(key);
            ElementModel? later = laterByKey.GetValueOrDefault(key);

            if (earlier is not null && later is not null)
            {
                ElementFlags flags = new(
                    Added: false,
                    Removed: false,
                    Renamed: RenameKind.None,
                    Cardinality: earlier.Min != later.Min
                        || !string.Equals(earlier.MaxString, later.MaxString, StringComparison.Ordinal),
                    Type: !ReleaseModel.TypesEqual(earlier.Types, later.Types),
                    Target: !ReleaseModel.SetsEqual(earlier.TargetProfiles, later.TargetProfiles));

                if (flags.Any)
                {
                    rows.Add(new ElementRow(
                        earlier.Path, later.Path, flags,
                        ElementSummary.Describe(earlier, later, flags, laterLabel)));
                }
            }
            else if (later is not null)
            {
                addedLeftovers.Add(later);
            }
            else if (earlier is not null)
            {
                removedLeftovers.Add(earlier);
            }
        }

        ElementRenameResult resolved =
            ElementRenameDetector.Resolve(removedLeftovers, addedLeftovers, laterLabel, confirmedFieldRenames);
        rows.AddRange(resolved.Rows);

        rows.Sort(static (a, b) => string.CompareOrdinal(a.SortPath, b.SortPath));
        return rows;
    }

    /// <summary>
    /// Builds the normalized-key → raw-element index for a structure, and the set of keys that
    /// are locally meaningful (not purely inherited). Slices and the root element are excluded;
    /// the first raw element wins on a normalized-key collision.
    /// </summary>
    private static Dictionary<string, ElementModel> BuildIndex(
        StructureModel structure, ReleaseModel release, out HashSet<string> meaningfulKeys)
    {
        Dictionary<string, ElementModel> byKey = new(StringComparer.Ordinal);
        meaningfulKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (ElementModel element in structure.Elements)
        {
            if (element.SliceName is not null || element.NormalizedKey.Length == 0)
            {
                continue;
            }
            byKey.TryAdd(element.NormalizedKey, element);
            if (!release.IsPurelyInherited(element))
            {
                meaningfulKeys.Add(element.NormalizedKey);
            }
        }
        return byKey;
    }
}

/// <summary>Builds the human-readable <c>Summary</c> prose for an element-diff row.</summary>
internal static class ElementSummary
{
    public static string Describe(
        ElementModel? earlier, ElementModel? later, ElementFlags flags, string laterLabel)
    {
        List<string> parts = [];

        if (flags.Renamed != RenameKind.None && earlier is not null && later is not null)
        {
            parts.Add($"renamed from {earlier.RootRelativePath}");
        }

        if (flags.Added)
        {
            parts.Add($"Added in {laterLabel}");
        }
        if (flags.Removed)
        {
            parts.Add($"Removed in {laterLabel}");
        }

        if (flags.Cardinality && earlier is not null && later is not null)
        {
            parts.Add($"{earlier.Cardinality} → {later.Cardinality}");
        }
        if (flags.Type && earlier is not null && later is not null)
        {
            parts.Add(DescribeTypes(earlier, later));
        }
        if (flags.Target && earlier is not null && later is not null)
        {
            parts.Add(DescribeTargets(earlier, later));
        }

        return string.Join("; ", parts);
    }

    private static string DescribeTypes(ElementModel earlier, ElementModel later)
    {
        List<string> earlierNames = DistinctOrdered(earlier.Types.Select(t => t.Name));
        List<string> laterNames = DistinctOrdered(later.Types.Select(t => t.Name));

        if (!SequenceSetEqual(earlierNames, laterNames))
        {
            return $"{string.Join("|", earlierNames)} → {string.Join("|", laterNames)}";
        }

        // Same type names; the difference is in declared profiles.
        HashSet<string> earlierProfiles = ProfileLocals(earlier.Types);
        HashSet<string> laterProfiles = ProfileLocals(later.Types);
        List<string> fragments = [];
        foreach (string added in laterProfiles.Where(p => !earlierProfiles.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
        {
            fragments.Add($"+{added} profile");
        }
        foreach (string removed in earlierProfiles.Where(p => !laterProfiles.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
        {
            fragments.Add($"-{removed} profile");
        }
        return fragments.Count > 0 ? string.Join(", ", fragments) : "type change";
    }

    private static string DescribeTargets(ElementModel earlier, ElementModel later)
    {
        HashSet<string> earlierTargets = new(earlier.TargetProfiles.Select(Local), StringComparer.Ordinal);
        HashSet<string> laterTargets = new(later.TargetProfiles.Select(Local), StringComparer.Ordinal);
        List<string> fragments = [];
        foreach (string added in laterTargets.Where(t => !earlierTargets.Contains(t)).OrderBy(t => t, StringComparer.Ordinal))
        {
            fragments.Add($"+{added} target");
        }
        foreach (string removed in earlierTargets.Where(t => !laterTargets.Contains(t)).OrderBy(t => t, StringComparer.Ordinal))
        {
            fragments.Add($"-{removed} target");
        }
        return fragments.Count > 0 ? string.Join(", ", fragments) : "target change";
    }

    private static HashSet<string> ProfileLocals(IReadOnlyList<ElementType> types)
    {
        HashSet<string> set = new(StringComparer.Ordinal);
        foreach (ElementType t in types)
        {
            if (!string.IsNullOrEmpty(t.Profile))
            {
                set.Add(Local(t.Profile));
            }
        }
        return set;
    }

    private static List<string> DistinctOrdered(IEnumerable<string> values)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string v in values)
        {
            if (seen.Add(v))
            {
                result.Add(v);
            }
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static bool SequenceSetEqual(List<string> a, List<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Last path segment of a canonical URL (after the final <c>/</c> or <c>#</c>).</summary>
    internal static string Local(string url)
    {
        int slash = url.LastIndexOfAny(['/', '#']);
        return slash < 0 ? url : url[(slash + 1)..];
    }
}
