using FhirAugury.Tools.FhirXverElementDiff.Model;

namespace FhirAugury.Tools.FhirXverElementDiff.Diff;

/// <summary>The rows produced from a structure's residual pure add/remove leftovers.</summary>
internal sealed record ElementRenameResult(IReadOnlyList<ElementRow> Rows);

/// <summary>
/// Resolves element-level renames over the residual removed/added leftovers of one mapped
/// structure, in priority order:
/// <list type="number">
/// <item>Caller-supplied ticket-confirmed field renames → <c>Confirmed</c> (<c>Y</c>).</item>
/// <item>Backbone renames: a residual parent whose subtree matches another residual parent's
/// subtree (same depth + parent, child-suffix Jaccard ≥ threshold). The old subtree is
/// prefix-rewritten and re-joined so the children are not reported as all-remove/all-add.</item>
/// <item>Leaf suspected renames: a lone residual removed and a lone residual added under the
/// same parent with identical facets → <c>Suspected</c> (<c>Y?</c>).</item>
/// </list>
/// Choice split/merge (<c>foo[x]</c> ↔ several <c>fooString</c>/<c>fooReference</c>) is never
/// force-paired: each element keeps its own add/remove row, annotated <c>(choice split)</c> /
/// <c>(choice merge)</c> for manual review. Anything still unmatched becomes a plain
/// Added/Removed row.
/// </summary>
internal static class ElementRenameDetector
{
    private const double BackboneJaccardThreshold = 0.5;

    public static ElementRenameResult Resolve(
        IReadOnlyList<ElementModel> removed,
        IReadOnlyList<ElementModel> added,
        string laterLabel,
        IReadOnlySet<(string OldKey, string NewKey)>? confirmedFieldRenames = null)
    {
        Dictionary<string, ElementModel> rem = new(StringComparer.Ordinal);
        foreach (ElementModel e in removed)
        {
            rem[e.NormalizedKey] = e;
        }
        Dictionary<string, ElementModel> add = new(StringComparer.Ordinal);
        foreach (ElementModel e in added)
        {
            add[e.NormalizedKey] = e;
        }

        HashSet<string> usedRem = new(StringComparer.Ordinal);
        HashSet<string> usedAdd = new(StringComparer.Ordinal);
        List<ElementRow> rows = [];

        // (1) Ticket-confirmed field renames.
        if (confirmedFieldRenames is not null)
        {
            foreach ((string oldKey, string newKey) in confirmedFieldRenames)
            {
                if (usedRem.Contains(oldKey) || usedAdd.Contains(newKey))
                {
                    continue;
                }
                if (rem.TryGetValue(oldKey, out ElementModel? oldEl)
                    && add.TryGetValue(newKey, out ElementModel? newEl))
                {
                    rows.Add(RenameRow(oldEl, newEl, RenameKind.Confirmed, laterLabel));
                    usedRem.Add(oldKey);
                    usedAdd.Add(newKey);
                }
            }
        }

        // (2) Backbone renames (outer parents first).
        List<ElementModel> removedBackbones = BackboneParents(removed);
        removedBackbones.Sort(static (a, b) => Depth(a.RootRelativePath).CompareTo(Depth(b.RootRelativePath)));
        foreach (ElementModel oldParent in removedBackbones)
        {
            if (usedRem.Contains(oldParent.NormalizedKey))
            {
                continue;
            }

            HashSet<string> oldChildSuffixes = ChildSuffixes(oldParent, removed);
            ElementModel? bestParent = null;
            double bestScore = BackboneJaccardThreshold;
            foreach (ElementModel newParent in added)
            {
                if (usedAdd.Contains(newParent.NormalizedKey)
                    || Depth(newParent.RootRelativePath) != Depth(oldParent.RootRelativePath)
                    || !string.Equals(ParentPrefix(newParent.RootRelativePath), ParentPrefix(oldParent.RootRelativePath), StringComparison.Ordinal))
                {
                    continue;
                }
                double score = Jaccard(oldChildSuffixes, ChildSuffixes(newParent, added));
                if (score > bestScore)
                {
                    bestScore = score;
                    bestParent = newParent;
                }
            }

            if (bestParent is null)
            {
                continue;
            }

            rows.Add(RenameRow(oldParent, bestParent, RenameKind.Suspected, laterLabel));
            usedRem.Add(oldParent.NormalizedKey);
            usedAdd.Add(bestParent.NormalizedKey);

            string oldPrefix = oldParent.RootRelativePath + ".";
            string newPrefix = bestParent.RootRelativePath + ".";
            foreach (ElementModel child in removed)
            {
                if (usedRem.Contains(child.NormalizedKey)
                    || !child.RootRelativePath.StartsWith(oldPrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                string rewrittenRelative = newPrefix + child.RootRelativePath[oldPrefix.Length..];
                string rewrittenKey = ElementModel.ComputeNormalizedKey(rewrittenRelative);
                if (add.TryGetValue(rewrittenKey, out ElementModel? newChild) && !usedAdd.Contains(rewrittenKey))
                {
                    rows.Add(RenameRow(child, newChild, RenameKind.Suspected, laterLabel));
                    usedRem.Add(child.NormalizedKey);
                    usedAdd.Add(rewrittenKey);
                }
            }
        }

        // (3) Leaf suspected renames: a lone unused removed + lone unused added under one parent.
        foreach (string parent in ParentPrefixes(removed).Concat(ParentPrefixes(added)).Distinct())
        {
            List<ElementModel> remSiblings = [.. removed.Where(e => !usedRem.Contains(e.NormalizedKey) && string.Equals(ParentPrefix(e.RootRelativePath), parent, StringComparison.Ordinal))];
            List<ElementModel> addSiblings = [.. added.Where(e => !usedAdd.Contains(e.NormalizedKey) && string.Equals(ParentPrefix(e.RootRelativePath), parent, StringComparison.Ordinal))];
            if (remSiblings.Count == 1 && addSiblings.Count == 1
                && ReleaseModel.FacetsEqual(remSiblings[0], addSiblings[0]))
            {
                rows.Add(RenameRow(remSiblings[0], addSiblings[0], RenameKind.Suspected, laterLabel));
                usedRem.Add(remSiblings[0].NormalizedKey);
                usedAdd.Add(addSiblings[0].NormalizedKey);
            }
        }

        // (4) Choice split/merge notes over the still-unused leftovers.
        Dictionary<string, string> notes = new(StringComparer.Ordinal);
        MarkChoiceGroups(removed, added, usedRem, usedAdd, isSplit: true, notes);
        MarkChoiceGroups(added, removed, usedAdd, usedRem, isSplit: false, notes);

        // (5) Remaining leftovers → plain Removed / Added rows.
        foreach (ElementModel e in removed)
        {
            if (usedRem.Contains(e.NormalizedKey))
            {
                continue;
            }
            ElementFlags flags = new(false, true, RenameKind.None, false, false, false);
            rows.Add(new ElementRow(e.Path, null, flags,
                AppendNote($"Removed in {laterLabel}", notes.GetValueOrDefault(e.Path))));
        }
        foreach (ElementModel e in added)
        {
            if (usedAdd.Contains(e.NormalizedKey))
            {
                continue;
            }
            ElementFlags flags = new(true, false, RenameKind.None, false, false, false);
            rows.Add(new ElementRow(null, e.Path, flags,
                AppendNote($"Added in {laterLabel}", notes.GetValueOrDefault(e.Path))));
        }

        return new ElementRenameResult(rows);
    }

    private static ElementRow RenameRow(
        ElementModel earlier, ElementModel later, RenameKind kind, string laterLabel)
    {
        ElementFlags flags = new(
            Added: false,
            Removed: false,
            Renamed: kind,
            Cardinality: earlier.Min != later.Min
                || !string.Equals(earlier.MaxString, later.MaxString, StringComparison.Ordinal),
            Type: !ReleaseModel.TypesEqual(earlier.Types, later.Types),
            Target: !ReleaseModel.SetsEqual(earlier.TargetProfiles, later.TargetProfiles));
        return new ElementRow(earlier.Path, later.Path, flags,
            ElementSummary.Describe(earlier, later, flags, laterLabel));
    }

    /// <summary>
    /// Flags choice split (<c>foo[x]</c> in <paramref name="choiceSide"/> → ≥2
    /// <c>fooString</c>/<c>fooReference</c> in <paramref name="expandedSide"/>) — or, when
    /// <paramref name="isSplit"/> is false, choice merge — annotating each participating
    /// unused element with the note. Participants stay as separate add/remove rows.
    /// </summary>
    private static void MarkChoiceGroups(
        IReadOnlyList<ElementModel> choiceSide,
        IReadOnlyList<ElementModel> expandedSide,
        HashSet<string> usedChoice,
        HashSet<string> usedExpanded,
        bool isSplit,
        Dictionary<string, string> notes)
    {
        string note = isSplit ? "(choice split)" : "(choice merge)";
        foreach (ElementModel choice in choiceSide)
        {
            if (usedChoice.Contains(choice.NormalizedKey)
                || !choice.Name.EndsWith("[x]", StringComparison.Ordinal))
            {
                continue;
            }
            string baseName = choice.Name[..^3];
            string parent = ParentPrefix(choice.RootRelativePath);
            List<ElementModel> expansions = [.. expandedSide.Where(e =>
                !usedExpanded.Contains(e.NormalizedKey)
                && string.Equals(ParentPrefix(e.RootRelativePath), parent, StringComparison.Ordinal)
                && e.Name.Length > baseName.Length
                && e.Name.StartsWith(baseName, StringComparison.Ordinal))];

            if (expansions.Count < 2)
            {
                continue;
            }

            notes[choice.Path] = note;
            foreach (ElementModel expansion in expansions)
            {
                notes[expansion.Path] = note;
            }
        }
    }

    private static string AppendNote(string summary, string? note) =>
        note is null ? summary : $"{summary} {note}";

    /// <summary>Elements that have at least one residual descendant on their own side.</summary>
    private static List<ElementModel> BackboneParents(IReadOnlyList<ElementModel> side)
    {
        List<ElementModel> parents = [];
        foreach (ElementModel candidate in side)
        {
            string prefix = candidate.RootRelativePath + ".";
            if (side.Any(other => !ReferenceEquals(other, candidate)
                && other.RootRelativePath.StartsWith(prefix, StringComparison.Ordinal)))
            {
                parents.Add(candidate);
            }
        }
        return parents;
    }

    private static HashSet<string> ChildSuffixes(ElementModel parent, IReadOnlyList<ElementModel> side)
    {
        string prefix = parent.RootRelativePath + ".";
        HashSet<string> suffixes = new(StringComparer.Ordinal);
        foreach (ElementModel e in side)
        {
            if (e.RootRelativePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                suffixes.Add(e.RootRelativePath[prefix.Length..]);
            }
        }
        return suffixes;
    }

    private static IEnumerable<string> ParentPrefixes(IReadOnlyList<ElementModel> side) =>
        side.Select(e => ParentPrefix(e.RootRelativePath)).Distinct();

    private static string ParentPrefix(string rootRelative)
    {
        int dot = rootRelative.LastIndexOf('.');
        return dot < 0 ? string.Empty : rootRelative[..dot];
    }

    private static int Depth(string rootRelative) =>
        rootRelative.Length == 0 ? 0 : rootRelative.Count(c => c == '.') + 1;

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return 0.0;
        }
        int intersection = a.Count(b.Contains);
        int union = a.Count + b.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }
}
